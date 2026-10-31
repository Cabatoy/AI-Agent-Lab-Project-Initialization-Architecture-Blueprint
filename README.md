# AI Agent Lab

AI Agent Lab, birden fazla bağımsız yazılım projesini yapay zekâ ajanlarıyla analiz etmeyi, planlamayı, geliştirmeyi ve doğrulamayı hedefleyen yeniden kullanılabilir bir platform girişimidir. Platform; insan karar ve onaylarını korurken mühendislik işlerinin büyük bölümünü ajanlara devretmek için tasarlanacaktır.

İlk gerçek müşteri projesi bir pazaryeri uygulamasıdır. Pazaryeri, Agent Lab'ın kendisinden ayrı bir proje ve güvenlik sınırı olarak ele alınacaktır.

## Aşama

Depo, mimari belgelerin yanında Podman ile çalıştırılabilen bir local prototip içerir. Bu prototip üretim dağıtımı değildir; AWS provisioning yapılmamıştır.

## Belgeler

- [Proje vizyonu](docs/00-project-vision.md)
- [Mimari ilkeler](docs/01-architecture-principles.md)
- [Mevcut kararlar](docs/02-current-decisions.md)
- [Açık sorular](docs/03-open-questions.md)
- [Mimari taslak](docs/04-architecture-blueprint.md)
- [Uygulama yol haritası](docs/05-implementation-roadmap.md)
- [Maliyet ve altyapı](docs/06-cost-and-infrastructure.md)
- [Devam noktası ve yeni sohbet rehberi](docs/continuation.md)
- [Local Copilot akış rehberi](docs/local-onboarding.md)
- [Mimari karar kayıtları](docs/decisions/README.md)

Gelecekteki AI coding oturumlarının proje bağlamı için [.github/copilot-instructions.md](.github/copilot-instructions.md) dosyasına bakın.

## Depoyu indirdikten sonra

1. Depoyu klonlayıp IDE'de açın. VS Code'da depo kökünü **File > Open Folder** ile açın. Visual Studio'da repo kökündeki `AI-Agent-Lab.sln` dosyasını açın; solution Control Plane, Worker ve `run.me` projelerini içerir.
2. Önce dosya denetimi ve sohbet handoff'u için `run.me` rehberini başlatın. Bu aşamalarda Podman çalıştırılmaz.
3. VS Code repo talimatlarını `.github/copilot-instructions.md` dosyasından yükler. Visual Studio sürümüne göre davranış değişebileceğinden Copilot Chat'ten bu dosyayı ve [devam rehberini](docs/continuation.md) açıkça okumasını isteyin.
4. Podman durum kontrolü ancak rehberde `approve-podman` ile açıkça onaylandıktan sonra yapılır. Bu onay yalnızca salt okunur kontroller içindir; machine/stack başlatma veya ayar değiştirme için ayrıca onay gerekir.

Tüm .NET projelerini terminalden derlemek için repo kökünde `dotnet build AI-Agent-Lab.sln` çalıştırın.

Yeni bir Copilot sohbetinde `docs/continuation.md` dosyasını okuyup oradaki sıradaki kontrol noktasından devam etmesini isteyin. Sohbet geçmişi Git'e kaydedilmez; dosya güncel proje handoff'unun kalıcı kaynağıdır.

## Local Podman kurulumu

Önkoşul: Podman Desktop ve `podman compose` tarafından kullanılabilen bir Compose provider (ör. `podman-compose` veya Docker Compose v2 CLI) kurulu olmalı. Compose provider yalnızca istemci aracıdır; container engine Podman olarak kalır. İlk kurulumda repo scripti Podman machine yoksa oluşturur, durmuşsa başlatır; Windows'ta user-mode networking'i yalnızca ayrıca onaylanırsa, WSL ortamında `pids` cgroup delegasyonunu gerektiğinde ayarlar.

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

- `control-plane` -> `http://localhost:8080/health`
- `agent-worker` -> Redis kuyruktan task alır ve status günceller
- `postgres` -> `localhost:5432`
- `redis` -> `localhost:6379`

İlk depo incelemesi, handoff ve onaylı Podman durum kontrolü için depo kökünde Windows'ta `./run.me.ps1`, macOS/Linux'ta `bash ./run.me` çalıştırın. Bu rehber stack'i başlatmaz. Sonradan ayrıca onaylanan local task smoke test'i ve komutlar için [local onboarding](docs/local-onboarding.md) sayfasına bakın.

İstekleri elle denemek için:

1. Task oluştur:
   - `POST <CONTROL_PLANE_URL>/tasks`
   - Body: `{ "title": "İlk görev", "description": "Local workflow testi" }`
2. Task onayla (kuyruğa girer):
   - `POST <CONTROL_PLANE_URL>/tasks/{taskId}/approve`
3. Task durumlarını izle:
   - `GET <CONTROL_PLANE_URL>/tasks/{taskId}`

Task durumu sırası: `Draft -> Approved -> Executing -> Completed`.

Durdurma:

- `podman compose down` container'ları durdurur ama PostgreSQL/Redis volume verilerini korur.

## Chat komutları

VS Code Chat'te `/new-build`, `/local-status`, `/rebuild-all` veya `/delete-all` promptlarından birini çalıştır.

- `new-build`: Gerekli network izninden sonra ilk kurulum veya mevcut veriyi koruyarak yeniden build/start.
- `local-status`: Container durumunu ve Control Plane adresini gösterir.
- `rebuild-all`: Compose kaynaklarını, image'ları ve task verisini silip cache'siz yeniden build eder. Chat'te ayrıca onay ister.
- `delete-all`: Bu repo compose projesinin container, image, network ve volume'lerini siler. Podman machine ve ilgisiz Podman kaynakları korunur. Chat'te ayrıca onay ister.

> **Veri uyarısı:** `rebuild-all` ve `delete-all` PostgreSQL/Redis volume'lerini kalıcı olarak siler. Chat promptları onay aldıktan sonra scriptleri şu bayrakla çağırır: Windows `-ConfirmDataRemoval`, macOS `--confirm-data-removal`.

> **WSL ağ uyarısı:** Windows `new-build`/`rebuild-all`, user-mode networking ayarını yalnızca `-AllowWslNetworkingChange` verildiğinde etkinleştirir. Bu ayar aktif Podman machine varken diğer WSL dağıtımlarının ağını da etkileyebilir.

Local komutlar AWS hesabına bağlanmaz veya cloud kaynağı oluşturmaz. Podman AI Lab ile local model denemesi isteğe bağlıdır; henüz Worker'a model sağlayıcısı entegre edilmemiştir.
