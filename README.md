# AI Agent Lab

AI Agent Lab, birden fazla bağımsız yazılım projesini yapay zekâ ajanlarıyla analiz etmeyi, planlamayı, geliştirmeyi ve doğrulamayı hedefleyen yeniden kullanılabilir bir platform girişimidir. Platform; insan karar ve onaylarını korurken mühendislik işlerinin büyük bölümünü ajanlara devretmek için tasarlanacaktır.

İlk gerçek müşteri projesi bir pazaryeri uygulamasıdır. Pazaryeri, Agent Lab'ın kendisinden ayrı bir proje ve güvenlik sınırı olarak ele alınacaktır.

## Aşama

Depo, mimari belgelerin yanında Podman ile çalıştırılabilen bir local prototip içerir. Bu prototip üretim dağıtımı değildir; AWS provisioning yapılmamıştır.

## Belgeler

- [Proje vizyonu](docs-md/00-project-vision.md)
- [Mimari ilkeler](docs-md/01-architecture-principles.md)
- [Mevcut kararlar](docs-md/02-current-decisions.md)
- [Açık sorular](docs-md/03-open-questions.md)
- [Mimari taslak](docs-md/04-architecture-blueprint.md)
- [Uygulama yol haritası](docs-md/05-implementation-roadmap.md)
- [Maliyet ve altyapı](docs-md/06-cost-and-infrastructure.md)
- [Devam noktası ve yeni sohbet rehberi](docs-md/continuation.md)
- [Local Copilot akış rehberi](docs-md/local-onboarding.md)
- [Mimari karar kayıtları](docs-md/decisions/README.md)

Gelecekteki AI coding oturumlarının proje bağlamı için [.github/copilot-instructions.md](.github/copilot-instructions.md) dosyasına bakın.

## Kanonik çalışma alanı

```text
src/
  ControlPlane/   Control Plane API ve migration kaynakları
  AgentWorker/    Worker
  NativeHost/     Salt okunur Native Host
  WebUI/          HIC Web UI
  AgentBridge/    Agent Bridge
tests/            .NET ve güvenlik sözleşmesi testleri
deploy/           Compose manifestleri ve izole runtime harness
scripts/          Local lifecycle ve HIC launcher'ları
docs-md/          Belge kaynakları ve devam noktası
docs/             HIC belgeleri ve yayımlanmış PDF'ler
tools/            run.me gibi yardımcı araçlar
HumanInControl.sln
hic.cmd
.local/           Git dışında yerel worktree, rapor, tanı ve yedek alanları
```

`.local/` Git tarafından yok sayılır; kişisel raporlar, çalışma ağaçları ve
sırlar commit edilmez. Eski checkout yolları yeni düzen için uyumluluk yolu
değildir. Mevcut checkout'u değiştirmeden çalışma yürütmek için ayrı bir Git
worktree kullanılabilir. Native Host yalnız `workspace.inspect` aksiyonuna
izin verir; task execution bu düzenlemeyle etkinleştirilmez.

Yayımlanmış PDF'ler önceki belge snapshot'ını korur ve eski kaynak yollarını
gösterebilir. Bu refactor'a üretilmiş PDF çıktıları dahil edilmez; güncel
yerleşim için bu bölüm ve Markdown kaynakları esas alınmalıdır.

## Depoyu indirdikten sonra

1. Depoyu klonlayıp IDE'de açın. VS Code'da depo kökünü **File > Open Folder** ile açın. Visual Studio'da repo kökündeki `HumanInControl.sln` dosyasını açın; solution Control Plane, Worker, Native Host, Agent Bridge, `run.me` ve .NET test projelerini içerir.
2. Önce dosya denetimi ve sohbet handoff'u için `run.me` rehberini başlatın. Bu aşamalarda Podman çalıştırılmaz. `approve-podman` onayından sonra rehber salt okunur Podman durum kontrolünü kendisi çalıştırır; sorun bulunursa `approve-setup` onayıyla `scripts/local.ps1`/`scripts/local.sh new-build` komutunu da kendisi başlatır. Rehberde `repo review`, `handoff check`, `podman check` ve `setup repair` komutlarıyla Chat mesajlarını dosyadan (`tools/run-me/prompts.tr.json`) da alıp kopyalayabilirsiniz. İstediğiniz adımı doğrudan çağırmak için `step <n>` komutunu da kullanabilirsiniz.

   Konsolu çalıştırmadan doğrudan Chat'e de `repo review`, `handoff check`, `podman check` ve `setup repair` komutları yazılabilir. Bu durumda da aynı onay kapıları geçerlidir (`podman check` için Podman onayı, `setup repair` için ayrı setup/onarım onayı).

3. VS Code repo talimatlarını `.github/copilot-instructions.md` dosyasından yükler. Visual Studio sürümüne göre davranış değişebileceğinden Copilot Chat'ten bu dosyayı ve [devam rehberini](docs-md/continuation.md) açıkça okumasını isteyin.
4. Podman durum kontrolü ancak rehberde `approve-podman` ile açıkça onaylandıktan sonra, rehberin kendisi tarafından çalıştırılır. Bu onay yalnızca salt okunur kontroller içindir. Sorun tespit edilirse rehber `approve-setup` ile ayrı onay ister ve onaylanırsa yalnızca `scripts/local.ps1`/`scripts/local.sh new-build` komutunu kendisi çalıştırır; başka lifecycle komutu (rebuild-all, delete-all) veya ayar değişikliği için ayrıca onay gerekir.

Tüm .NET projelerini terminalden derlemek için repo kökünde `dotnet build HumanInControl.sln` çalıştırın.

Yeni bir Copilot sohbetinde `docs-md/continuation.md` dosyasını okuyup oradaki sıradaki kontrol noktasından devam etmesini isteyin. Sohbet geçmişi Git'e kaydedilmez; dosya güncel proje handoff'unun kalıcı kaynağıdır.

## Local Podman kurulumu

Önkoşul: Podman Desktop ve `podman compose` tarafından kullanılabilen bir Compose provider (ör. `podman-compose` veya Docker Compose v2 CLI) kurulu olmalı. Compose provider yalnızca istemci aracıdır; container engine Podman olarak kalır. İlk kurulumda repo scripti Podman machine yoksa oluşturur, durmuşsa başlatır; Windows'ta user-mode networking'i yalnızca ayrıca onaylanırsa, WSL ortamında `pids` cgroup delegasyonunu gerektiğinde ayarlar.

Hızlı bootstrap için Windows'ta şu komutu kullanabilirsiniz:

```powershell
./scripts/local.ps1 bootstrap-dev
```

Bu komut stack'i kurar, AI Lab container bulunursa `ai-lab` alias'ını bağlar, seçilen LLM profile göre `.env` dosyasını günceller ve smoke test çalıştırır. Veriyi sıfırlayarak temiz başlangıç istenirse:

```powershell
./scripts/local.ps1 bootstrap-dev -Reset
```

`-Reset` modunda local volume verisi silinir.

İlk indirme akışında `run.me` önce dosyaları ve handoff'u kontrol ettirir, sonra yalnızca açık onayla Podman durumunu salt okunur inceler. Aşağıdaki lifecycle komutları machine/stack başlatabilir; bunları çalıştırmak için durum incelemesinden ayrı onay alın.

Windows PowerShell:

```powershell
./scripts/local.ps1 new-build
```

İlk kez Windows/WSL user-mode networking ayarı gerekiyorsa script durup etkiyi bildirir. Bu ayar Podman machine çalışırken diğer WSL dağıtımlarının ağını da etkileyebilir; etkisini onayladıktan sonra komutu `-AllowWslNetworkingChange` ile yeniden çalıştır.

macOS:

```bash
./scripts/local.sh new-build
```

Komut eski compose container/network nesnelerini kaldırıp image'ları build eder, stack'i başlatır ve erişilebilir Control Plane adresini yazdırır. PostgreSQL/Redis volume verisi korunur. Windows/WSL'de bu adres `localhost` yerine Podman machine IP'si olabilir.

Servisler:

- `control-plane` -> varsayılan `http://127.0.0.1:19480/health`; mevcut eski kurulumlar `8080` kullanabilir
- `agent-worker` -> varsayılan olarak kapalıdır; `manual-worker` profili ve ayrı açık onay gerektirir
- `postgres` -> Compose ağı içinde `postgres:5432`; host portu yayımlanmaz
- `redis` -> Compose ağı içinde `redis:6379`; host portu yayımlanmaz

İlk depo incelemesi, handoff ve onaylı Podman durum kontrolü için depo kökünde Windows'ta `./run.me.ps1`, macOS/Linux'ta `bash ./run.me` çalıştırın. Bu rehber stack'i başlatmaz. Sonradan ayrıca onaylanan local task smoke test'i ve komutlar için [local onboarding](docs-md/local-onboarding.md) sayfasına bakın.

İstekleri elle denemek için:

1. Task oluştur:
   - `POST <CONTROL_PLANE_URL>/tasks`
   - Body: `{ "title": "İlk görev", "description": "Local workflow testi" }`
2. Task onayla (kuyruğa girer):
   - `POST <CONTROL_PLANE_URL>/tasks/{taskId}/approve`
3. Task durumlarını izle:
   - `GET <CONTROL_PLANE_URL>/tasks/{taskId}`

Task durumu sırası: `Draft -> Approved -> Executing -> Completed`. Hata durumunda `Failed` aşamasına da geçilebilir.

Durdurma:

- `podman compose down` container'ları durdurur ama PostgreSQL/Redis volume verilerini korur.

## Local model provider (opsiyonel)

Worker, model çağrısı için opsiyonel provider iskeleti içerir. Varsayılan davranış `MODEL_PROVIDER=disabled` olduğu için model çağrısı yapılmaz.

`bootstrap-dev` komutunda LLM seçimini parametreyle yapabilirsiniz:

- PowerShell: `-LlmProvider auto|disabled|podman-ai-lab|ollama|openai-compatible`
- Bash: `--llm-provider=auto|disabled|podman-ai-lab|ollama|openai-compatible`

Etkileşimli terminalde PowerShell için `-LlmProvider`, Bash için `--llm-provider` verilmezse `bootstrap-dev`, servisleri başlattıktan sonra provider seçimini sorar. `podman-ai-lab` seçildiğinde çalışan model servislerini listeler ve seçim yaptırır; birden fazla servis varken `auto` seçilirse de hangi servisle devam edileceği sorulur. Seçilen servise `ai-lab` alias'ı bağlanır. Veri korumak için `-Reset`/`--reset` kullanmadan çalıştırabilirsiniz.

Opsiyonel ek parametreler:

- PowerShell: `-LlmEndpoint <url> -LlmModel <model> -LlmApiKey <key>`
- Bash: `--llm-endpoint=<url> --llm-model=<model> --llm-api-key=<key>`

PowerShell'de `openai-compatible` seçilip endpoint/model parametreleri verilmezse interaktif terminalde bu değerler ayrıca sorulur (input redirected durumda parametre zorunludur).

Örnekler:

```powershell
./scripts/local.ps1 bootstrap-dev -LlmProvider podman-ai-lab
./scripts/local.ps1 bootstrap-dev -LlmProvider ollama -LlmEndpoint http://host.containers.internal:11434 -LlmModel llama3.1
./scripts/local.ps1 bootstrap-dev -LlmProvider openai-compatible -LlmEndpoint https://api.openai.com -LlmModel gpt-4o-mini -LlmApiKey <SECRET>
```

`auto` seçimi AI Lab alias bulunursa `podman-ai-lab`, bulunamazsa `disabled` seçer. `podman-ai-lab` için model id otomatik algılanamazsa güvenli fallback olarak `.env` `disabled` yazılır.

`bootstrap-dev` smoke test sonunda script, doğrulama için hem `.env` içindeki model ayarını hem de `agentlab-worker` container'ındaki runtime model ayarını (provider/endpoint/model) çıktı olarak gösterir.

`new-build` ve `rebuild-all` komutları, Podman AI Lab container'ını bulabilirse compose ağına `ai-lab` alias'ı ile bağlamayı dener. Böylece rastgele container adlarına bağımlılık azaltılır. Worker model çağrısında önce `chat/completions`, ardından `completions` fallback'i dener; geçici hatalarda kısa retry uygular.

Worker yalnızca `disabled`, `podman-ai-lab`, `ollama` ve `openai-compatible` provider değerlerini kabul eder. Desteklenmeyen provider, eksik endpoint/model veya mutlak HTTP(S) olmayan ya da URL içine gömülü kimlik bilgisi taşıyan endpoint model istemcisini kapalı tutar; görev sonucu hangi ayarın düzeltilmesi gerektiğini açıkça belirtir. Bu yapılandırma uyarıları ve hata metinleri URL veya anahtar değerlerini içermez. Provider yanıtları ve bağlantı hataları ayrı bir akıştır; hata içerikleri görev sonucuna ve dead-letter kaydına aktarılabilir, bu nedenle aynı sanitizasyon garantisi bunlar için geçerli değildir. Kimlik doğrulama için `MODEL_API_KEY` kullanın.

`.env` içindeki kişisel/secret değerleri paylaşmayın.

## Chat komutları

VS Code Chat'te `/new-build`, `/local-status`, `/rebuild-all` veya `/delete-all` promptlarından birini çalıştır.

- `new-build`: Gerekli network izninden sonra ilk kurulum veya mevcut veriyi koruyarak yeniden build/start.
- `local-status`: Container durumunu ve Control Plane adresini gösterir.
- `rebuild-all`: Compose kaynaklarını, image'ları ve task verisini silip cache'siz yeniden build eder. Chat'te ayrıca onay ister.
- `delete-all`: Bu repo compose projesinin container, image, network ve volume'lerini siler. Podman machine ve ilgisiz Podman kaynakları korunur. Chat'te ayrıca onay ister.

> **Veri uyarısı:** `rebuild-all` ve `delete-all` PostgreSQL/Redis volume'lerini kalıcı olarak siler. Chat promptları onay aldıktan sonra scriptleri şu bayrakla çağırır: Windows `-ConfirmDataRemoval`, macOS `--confirm-data-removal`.

> **WSL ağ uyarısı:** Windows `new-build`/`rebuild-all`, user-mode networking ayarını yalnızca `-AllowWslNetworkingChange` verildiğinde etkinleştirir. Bu ayar aktif Podman machine varken diğer WSL dağıtımlarının ağını da etkileyebilir.

Local komutlar AWS hesabına bağlanmaz veya cloud kaynağı oluşturmaz. Podman AI Lab ile local model denemesi isteğe bağlıdır; Worker'daki provider katmanı yalnızca local prototip denemesi içindir ve production sağlayıcı kararı değildir.
