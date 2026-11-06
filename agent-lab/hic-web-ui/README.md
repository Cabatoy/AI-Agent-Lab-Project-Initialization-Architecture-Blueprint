# HIC Web UI — Yerel kontrol paneli

## Yönetici özeti

Bu ilk, **salt okunur** HIC ekranı, mevcut Control Plane'in task ve plan
bilgilerini gösterir. Control Plane tek task/execution otoritesidir; bu
uygulama ayrı bir task deposu, scheduler veya backend eklemez. Ajan yönetimi,
provider routing ve onay işlemleri uygulanmamıştır. R3/R4 recovery ve tam P9
approval workflow hâlâ açıktır.

## Teknik ek

### Önkoşullar ve çalıştırma

- Node.js 20.19+ veya 22.12+ ve npm.
- Ayrı olarak, önceden çalıştırılmış ve yalnız yerel erişime uygun biçimde
  yapılandırılmış Control Plane. UI mevcut stack'i başlatmaz veya değiştirmez.
- Control Plane erişilemiyorsa UI yine açılır ve hataları gösterir.

Bu klasörde Windows PowerShell, macOS veya Linux terminalinden:

```text
npm ci
npm run dev
```

Tarayıcı: **http://127.0.0.1:5173**. Port kullanımda ise sunucu otomatik başka
porta geçmek yerine hata verir. Durdurmak için terminalde Ctrl+C kullanın.
Geliştirme sunucusu yalnız `127.0.0.1` adresine bağlanır.

### API yapılandırması

Varsayılan API adresi `http://127.0.0.1:8080`'dir. Başka bir yerel port için
sunucu başlamadan önce `HIC_API_URL` ayarlayın:

```powershell
$env:HIC_API_URL = 'http://127.0.0.1:18080'
npm run dev
```

```bash
HIC_API_URL=http://127.0.0.1:18080 npm run dev
```

Yalnız HTTP, tam IPv4 loopback adresi `127.0.0.1` ve port kabul edilir;
credential, path, query, hostname ve uzak adresler reddedilir. Bu ayar yalnız
Vite geliştirme sürecinde kullanılır, browser bundle'ına konmaz. Podman
machine IP'si veya uzak bir API bu sürümde desteklenmez; güvenli bir loopback
erişimi ayrıca mevcut değilse altyapıyı değiştirmeden API erişimi kullanılamaz.

Geliştirme proxy'si yalnız şu GET isteklerini iletir: `/health`, `/tasks`,
`/tasks/{guid}` ve `/plans`. Diğer yöntemler, yollar ve query string'ler
reddedilir; browser credential/header'ları upstream'e aktarılmaz. İstekler
zaman aşımına uğrar. Bu proxy bir authentication veya authorization mekanizması
değildir; Control Plane'i doğrudan erişime karşı korumaz.

**Güvenlik sınırı:** Bu yerel geliştirme aracı production için değildir.
Kullanılan Control Plane portunun ayrıca yalnız loopback'e bağlı ve yetkisiz
ağ erişiminden korunmuş olması gerekir. Depodaki genel Compose port mapping'i
bu güvenceyi tek başına sağlamayabilir; bu UI onu değiştirmez. Geliştirme
sunucusunu `--host 0.0.0.0` ile açmayın, ters proxy ile internete sunmayın ve
tarayıcıya secret koymayın. Güvenilmeyen yerel kullanıcılar ve tarayıcı
eklentileri de yerel verilere erişebilir. Production için kimlik doğrulama,
yetkilendirme, TLS, izolasyon ve audit ayrıca tasarlanmalıdır.

### Kontroller

```text
npm run build
npm test
```

Build TypeScript kontrolünü ve Vite üretim bundle'ını oluşturur. Bundle tek
başına API proxy'si içermez; desteklenen yerel kullanım `npm run dev`'dir.
Testler sentetik yanıtlarla çalışır; task veya altyapı oluşturmaz.

### Mevcut ekranlar ve sınırlar

- **Overview:** `/health`, `/tasks`, `/plans`; durum sayıları, son task'lar ve
  üç isteğin tümünün son başarılı refresh zamanı. Health yalnız API'nin health
  yanıtıdır, PostgreSQL/Redis/Worker sağlık garantisi değildir.
- **Tasks:** `/tasks` ve `/tasks/{id}`; mevcut durum, deneme sayısı, sonuç ve
  hata alanları. Audit/execution geçmişi bu read API'de mevcut değildir.
- **Plans:** `/plans`; gerçek plan alanları. Plan–task ilişkileri ve dependency
  graph API tarafından sunulmaz.
- **Agents**, **Models & Providers**, **Approvals:** yalnız gelecek yetenekleri
  açıklayan placeholder ekranlar; onay/ret düğmesi veya model çağrısı yoktur.
- Herhangi bir task mutasyonu, LLM çağrısı, GitHub yazma işlemi, secret,
  token/maliyet ölçümü, otomatik execution veya deployment yapılmaz.
