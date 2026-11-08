# HIC Web UI — Yerel kontrol paneli

## Yönetici özeti

Bu yerel HIC ekranı, mevcut Control Plane'in task ve plan bilgilerini gösterir.
**Command Center** ekranından doğal dilde iş tanımı yazılarak yalnızca **Draft**
durumunda task oluşturulabilir; Draft task onaylanmaz, kuyruğa alınmaz, ajan
veya model çalıştırılmaz. **Planning** ekranı Native Host'un tek yapılandırılmış
workspace'ini salt okunur kontrol eder ve yalnız tarayıcı belleğinde sürümlü plan
önizlemesi üretir; bu plan Control Plane'e yazılmaz ve çalıştırılamaz. Control
Plane tek task/execution otoritesidir; bu uygulama ayrı bir task deposu,
scheduler veya backend eklemez. Kimliği doğrulanmış plan onayı ve kalıcı audit
altyapısı mevcut olmadığından onay/ret işlemleri kapalıdır. R3/R4 recovery ve
tam P9 approval workflow hâlâ açıktır.

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
`/tasks/{guid}` ve `/plans`. Tek yazma isteği `POST /api/tasks`'tır (Draft
oluşturma). Diğer yöntemler, yollar (ör. `/approve`, `/retry`) ve query
string'ler reddedilir; browser cookie/credential/header'ları upstream'e
aktarılmaz. Tüm `/api` isteklerinde `Host` başlığı `127.0.0.1:<port>` olmalıdır
(DNS rebinding koruması); başka bir `Origin` ile gelen GET'ler reddedilir.

`POST /api/tasks` yalnız şu koşullarda iletilir:

- `Origin` tam olarak `http://127.0.0.1:<port>`, `Sec-Fetch-Site` yok veya
  `same-origin` ve `X-HIC-Request: command-center` başlığı mevcut (CSRF
  koruması).
- `Authorization`/`Proxy-Authorization` yok; `Content-Type: application/json`,
  sıkıştırılmamış gövde, en fazla 64 KiB, geçerli UTF-8 JSON.
- Gövde yalnız `title` (zorunlu, en fazla 200 karakter) ve `description`
  (zorunlu, en fazla 8.000 karakter) alanlarını içerir; proxy bu iki alanı
  yeniden serialize ederek iletir.
- Ret yanıtları `text/plain` ve `X-HIC-Proxy-Error: rejected` başlığıyla döner;
  kullanıcı girdisi yanıtta tekrarlanmaz.

Upstream ve gövde okuma zaman aşımı 8 saniyedir; bağlantı kurulamazsa 502
(`unreachable`), bağlantı yarıda kesilirse 502 (`interrupted`), zaman aşımında
504 döner. Bu proxy bir authentication veya authorization mekanizması
değildir; Control Plane'i doğrudan erişime karşı korumaz. **Control Plane'de
kimlik doğrulama yoktur**; bu nedenle API ve UI loopback dışına açılmamalıdır.

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
Testler sentetik yanıtlarla ve sahte upstream sunucusuyla çalışır; gerçek task\nveya altyapı oluşturmaz.

### Mevcut ekranlar ve sınırlar

- **Planning:** Native Host'tan yalnızca yapılandırılmış workspace için başarılı
  read-only Git checkout kontrolü alındıktan sonra bu tek repository seçilebilir.
  Plan, yerel deterministik bir şablondur; içerik, sürüm ve SHA-256 hash içerir.
  Provider/Worker durumları için probe yapılmaz; provider'lar doğrulanana kadar
  hazır sayılmaz. Plan önizleme olayları sadece sayfa belleğinde tutulur; bu
  güvenilir veya kalıcı audit değildir. Control Plane'de authenticated actor ve
  hash'e bağlı approval receipt olmadığından Approve devre dışıdır.
- **Command Center:** Request (zorunlu) ve Title (isteğe bağlı) alanları.
  Title boşsa isteğin ilk dolu satırından en fazla 80 karakterlik başlık
  türetilir. Enter yeni satır ekler; Ctrl+Enter (macOS'ta ⌘+Enter) veya düğme
  gönderir. Gönderim tek seferliktir (çift tıklama korumalı), otomatik retry
  yoktur. Başarıda "Created as Draft — not executed" mesajı, task ID'si ve
  task detayına geçiş düğmesi gösterilir. Kesin hatalarda girilen metin
  korunur. Sonucu belirsiz durumlarda (zaman aşımı, bağlantı kesilmesi,
  Control Plane 5xx, beklenmeyen yanıt) task oluşmuş olabilir: yeni gönderim,
  Tasks listesi başarıyla yenilenene kadar kilitlenir.
- **Overview:** `/health`, `/tasks`, `/plans`; durum sayıları, son task'lar ve
  üç isteğin tümünün son başarılı refresh zamanı. Health yalnız API'nin health
  yanıtıdır, PostgreSQL/Redis/Worker sağlık garantisi değildir.
- **Tasks:** `/tasks` ve `/tasks/{id}`; mevcut durum, deneme sayısı, sonuç ve
  hata alanları. Audit/execution geçmişi bu read API'de mevcut değildir.
- **Plans:** `/plans`; gerçek plan alanları. Plan–task ilişkileri ve dependency
  graph API tarafından sunulmaz.
- **Agents**, **Models & Providers**, **Approvals:** yalnız gelecek yetenekleri
  açıklayan placeholder ekranlar; plan approval/rejection veya model çağrısı
  yoktur.
- Draft task oluşturma dışında task mutasyonu (approve, retry, dispatch),
  LLM çağrısı, GitHub yazma işlemi, secret, token/maliyet ölçümü, otomatik
  execution veya deployment yapılmaz.


## Offline unsent input

Command Center keeps **one unsent text buffer** in this browser profile's `localStorage` while typing, even if Control Plane/WSL is offline. It is not a persisted Control Plane task, not encrypted, and must not contain secrets. It does not sync or submit automatically; only the explicit `Create Draft task` click sends a request. On a confirmed 201 Draft creation the local text is cleared. When a POST outcome cannot be confirmed, an `uncertain` lock is saved across page reloads to prevent blind retry until the real task list is checked. Browser/profile clearing, private browsing, storage denial and simultaneous tabs may affect availability; the UI warns when local saving fails. To access this feature, a new built UI must actually be running; a pre-existing 5180 dev server does not automatically update until it serves the updated checkout.
