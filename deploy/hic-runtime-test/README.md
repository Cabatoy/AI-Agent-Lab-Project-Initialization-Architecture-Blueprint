# HIC izole runtime test harness

## Yönetici Özeti

Bu harness yalnızca sentetik test verisiyle .NET 10 container'larını doğrulamak içindir. Üretim Compose dosyasını ve lifecycle scriptlerini değiştirmez. Hazırlanması runtime çalıştırma onayı değildir.

PowerShell 5.1+ ve Windows, çalışan ve önceden onaylanmış Podman machine, Compose provider ve build sırasında registry/NuGet erişimi gerekir. Machine başlatma veya ağ ayarı değiştirme otomasyonu yoktur. Statik `validate` komutu Podman'a veya ağa erişmez.

## İzolasyon

- Her çalıştırma için `hic-net10-` + 12 hex karakter proje adı seçilir; bütün komutlarda aynı ad/port kullanılır.
- Container adları Compose tarafından projeye göre üretilir. Yeni, proje kapsamlı PostgreSQL/Redis volume'ları ve `internal` ağ kullanılır.
- PostgreSQL, Redis ve stub host portu açmaz. API yalnızca `127.0.0.1` üzerinde yapılandırılan portu açar.
- `.env` yerine yorum dışında boş `empty.env` kullanılır; model konfigürasyonu interpolation içermez. Sabit parola yalnızca sentetik test credential'ıdır; gerçek credential eklemeyin.
- Worker `openai-compatible` adapter'ını **yalnızca test ağındaki deterministik stub** ile kullanır; cloud model erişimi yapılandırılmaz. Internal ağ uygulama container'ları için dış erişimi engellemeyi hedefler; build ağ erişimini engellemez.
- API'nin sabit `agentlab:task-queue` anahtarı Worker'da korunur; izolasyon ayrı Redis instance'ıyla sağlanır.
- Kaynakların özel harness ve Compose proje etiketleri, container mount'ları ve ağ bağlantıları denetlenir. Var olan kaynağın yerel state olmadan sahiplenilmesi reddedilir.
- Runtime state ve üretilen Compose dosyası `.state/<project>/` altında tutulur, Git'e dahil edilmez. Container inspect çıktısı, HTTP hata içeriği veya credential basılmaz.

## Teknik Ek — Offline doğrulama

```powershell
& .\deploy\hic-runtime-test\runtime-test.ps1 validate
& .\deploy\hic-runtime-test\runtime-test.offline.tests.ps1
```

İkinci dosya yalnız harness fonksiyonlarını PowerShell AST üzerinden yükler;
runtime entry point'i çalıştırmaz. SQL/Compose/native işlemleri mock edilir.
Eksik migration, değişen initializer/version, sıra/state hataları, belirsiz SQL
ve API sonucu, cloud endpoint, API portu ve ownership kapıları test edilir.
Bu testler gerçek PostgreSQL veya Podman doğrulaması değildir.

A2.1 offline doğrulaması Windows PowerShell 5.1 ile geçti: `validate` PASS,
34 offline test PASS; whitespace ve Markdown bağlantı/fence kontrolleri PASS.
Bu doğrulamada hiçbir Podman komutu, SQL veya container E2E çalıştırılmadı.

## R1 legacy akışı

`start` komutunun anlamı değişmedi: dependencies ve API birlikte build/start
edilir; Worker başlamaz. **R2 kaynaklarında bu komut herhangi bir Podman
çağrısından önce hata verir**, `start-dependencies -> prepare-schema -> start-api`
yolunu gösterir. R1 akışı yalnız v2 startup kapısı bulunmayan, authoritative
initializer biçimi desteklenen R1 checkout'unda kullanılabilir. R2 script'i
eski R1 state'ini R2'ye dönüştürmez; mevcut projeyi yeniden kullanmayın.

Tarihsel R1 komut sırası (yeni runtime izni değildir):

```powershell
$project = 'hic-net10-' + ([guid]::NewGuid().ToString('N').Substring(0, 12))
$port = 18080
$script = '.\deploy\hic-runtime-test\runtime-test.ps1'

& $script validate
& $script preflight -Project $project -ApiPort $port
& $script start -Project $project -ApiPort $port -ApproveRuntime
& $script readiness -Project $project -ApiPort $port
& $script persistence -Project $project -ApiPort $port -ApproveRuntime
# R1'de Worker image'ını aynı generated Compose config/project ile ayrıca build edin.
& $script worker -Project $project -ApiPort $port -ApproveRuntime
```

## R2 staged akışı

**A2.2 izole R2 container E2E geçti**; kapsam ve ölçüm sınırları
[R2 runtime checkpoint'inde](../../docs/human-in-control/R2_EXECUTION_OWNERSHIP.md#a22-izole-runtime-checkpoint--6-kasım-2026)
kayıtlıdır. Her yeni çalıştırma için ayrı açık runtime/SQL/sentetik veri onayı
gerekir; A2.1 yalnız kod ve offline doğrulamaydı.

```powershell
$project = 'hic-net10-' + ([guid]::NewGuid().ToString('N').Substring(0, 12))
$port = 18082 # Yalnız aday; preflight kullanılabilirliğini kontrol eder.
$script = '.\deploy\hic-runtime-test\runtime-test.ps1'

& $script validate
& $script preflight -Project $project -ApiPort $port
& $script start-dependencies -Project $project -ApiPort $port -ApproveRuntime
& $script prepare-schema -Project $project -ApiPort $port -ApproveRuntime
& $script start-api -Project $project -ApiPort $port -ApproveRuntime
& $script readiness -Project $project -ApiPort $port
& $script persistence -Project $project -ApiPort $port -ApproveRuntime
& $script worker -Project $project -ApiPort $port -ApproveRuntime
```

Her adımı ayrı çalıştırıp başarıdan sonra ilerleyin. `start-api` API ve Worker
image'larını mevcut checkout'tan build eder; yalnız API container'ını
`--no-deps` ile başlatır. Worker ancak `worker` aksiyonunda, kayıtlı Draft task
onaylanıp kuyruk UUID'si doğrulandıktan sonra başlar. Build revision label veya
binary hash kanıtı otomatik üretilmez; R2 E2E raporunda ayrıca doğrulanmalıdır.

### Base schema kaynağı ve v2 transaction

Tek authoritative base schema [Control Plane initializer](../../agent-lab/control-plane/Program.cs)
içindeki `InitializeSchemaAsync` metodudur. Harness bu metodun **tam, bilinen**
biçimini (tek bağlantı ve tek sabit C# verbatim SQL literal'i) doğrular,
literal'i okuyup C# çift tırnak escaping'ini çözer. SQL kopyası veya bağımsız
schema dosyası tutulmaz. Metot biçimi/dinamik SQL değişirse harness fail-closed
durur; regex genel bir C# parser değildir ve sessizce uyarlama yapmaz.
Kaynak dosya ve migration SHA-256 değerleri dependency startup state'ine
kaydedilir; hazırlık/start-api öncesi değişmiş kaynak reddedilir.

`prepare-schema`, yalnız fresh staged projede; etiketler, PostgreSQL/Redis
container ID'leri, named volume kimlikleri, internal network ID'si,
`hic_test` database/current role/owner ve boş kullanıcı tabloları doğrulandıktan
sonra çalışır. API veya Worker container'ı (durmuş olsa bile) bulunamaz.
Bu rol mevcut Compose'daki test bootstrap rolüdür; production least-privilege
migration veya servis authentication modeli olarak sunulmaz.

1. Authoritative base SQL, `psql -X --single-transaction -v ON_ERROR_STOP=1`
   ile stdin üzerinden uygulanır; üç base tablo doğrulanır.
2. Değiştirilmemiş
   [002-execution-ownership.sql](../../agent-lab/control-plane/Migrations/002-execution-ownership.sql)
   aynı fail-on-error yöntemiyle **ayrı explicit transaction** içinde uygulanır.
3. Version 2, generation kolonu/check, execution kolonları,
   primary/unique/foreign/check constraint'ler, audit execution foreign key'i
   ve tek aktif execution unique partial index doğrulanır.

API'nin v2 kontrolü kapatılmaz veya atlanmaz. SQL hatası/belirsiz sonuçta ikinci
transaction veya uygulama startup'ı otomatik denenmez.

### State ve kesinti davranışı

State sırası:
`dependencies-starting -> dependencies-ready -> schema-preparing -> schema-ready -> api-starting -> api-ready`.
Her mutation'dan **önce** in-progress aşaması diske yazılır. State/config Git
dışındaki mevcut `.state/<project>/` dizinindedir. Yalnız kesin tamamlanmış
aşamadan bir sonraki aşamaya geçilir; `prepare-schema` tekrar çalıştırılmaz.
Eksik/bozuk state, bilinmeyen SQL/commit sonucu, yarım build/start veya değişen
resource kimliğinde durun. Kaynakları koruyup inceleyin; state'i elle
`schema-ready` yapmayın, migration'ı tekrar denemeyin. Yeni deneme ayrı onayla
yeni benzersiz proje gerektirir. Transaction rollback olsa da aynı proje
otomatik sıfırlanmaz; base transaction başarılı/v2 başarısız olabilir.

Existing container/volume/network state olmadan sahiplenilmez. Herhangi bir
silme/reset veya otomatik cleanup yoktur. `stop` ayrıca açık onay gerektirir;
volume/network/veriyi silmez ve bu örnek akışın otomatik son adımı değildir.

Her adım başarısızsa sonraki adıma geçmeyin. `-ApproveRuntime`, ayrı insan onayının yerine geçmez; onaylandıktan sonra açık operasyon kapısıdır. `preflight`/`readiness` de Podman ve readiness için API çağrısı yaptığından ayrıca çalışma izni gerektirir.

### Beklenen sonuçlar

| Komut | Davranış / kanıt |
|---|---|
| validate | JSON/paths, ownership label, port, model endpoint ve queue konfigürasyonunu statik doğrular |
| preflight | Podman bağlantısını, var olan proje kaynaklarının sahipliğini ve boş host portunu denetler; servis başlatmaz |
| start | Projeye özel konfigürasyon üretir; yalnızca DB/Redis/stub/API build/start yapar; Worker başlamaz |
| start-dependencies | Yalnız fresh R2 projede DB/Redis/stub başlar; dependency kimlikleri/state kaydedilir |
| prepare-schema | API/Worker yokken empty test DB'ye authoritative base + explicit v2 migration uygular |
| start-api | Yalnız schema-ready ve doğrulanmış v2 ile API/Worker image build; sadece API start |
| readiness | Container ownership, dependency health, PostgreSQL `SELECT 1`, Redis PING ve API health yanıtını doğrular |
| persistence | Yeni Draft task oluşturur; UUID'yi kaydeder; PostgreSQL status ve API read-back doğrular |
| worker | Aynı Draft task'ı onaylar; kuyrukta tam doğru UUID'yi kontrol eder; Worker'ı başlatır; Completed/sabit çıktı/attempt 1/audit/boş queue ve DLQ bekler |
| stop | Yalnızca doğrulanmış test container ID'lerini 30 saniye grace süresiyle durdurur; volume/network/container/state silmez |

### Veri etkisi, rollback ve sınırlar

`start` yeni DB şeması, named volume, ağ ve image oluşturur; `persistence`/`worker` test DB ve queue'ya yazar. Önceki test verileri otomatik temizlenmez. Aynı state'te ikinci task veya otomatik retry yoktur; başarısız bir test inceleme için korunur. Yeni deneme için benzersiz yeni proje kullanın.

Rollback yalnızca `stop` ile test container'larını durdurmaktır. Kalıcı test verileri ve image'lar kalır; silme ayrı onay gerektirir. Machine, üretim container'ları, `.env`, PostgreSQL volume'ları ve AI Lab ağ bağlantıları değiştirilmez.

Bu sürüm yalnızca başarı yolu ve doğrudan DB/API read-back doğrular; container restart sonrası persistence, fault injection, retry/crash recovery, aktif iş sırasında cancellation, gerçek LLM ve Swagger/TLS/globalization kontrolleri uygulanmaz. Worker durdurma sırasında aktif işin recovery garantisi yoktur. Compose provider label/name/health ve internal network davranışı gerçek runtime'da ayrıca doğrulanmalıdır. Port bind başarısızlığı güvenli hata olarak durdurulur; localhost başarısızsa başka IP'ye fallback yapılmaz.

R2 ownership/fencing özel DB/API assertion'ları ve image/source kanıtı harness
dışındaki A2.2 doğrulamasında ayrıca alındı; script bunları otomatik kanıtlamaz.
Mevcut stub bağımsız HTTP sayacı sunmaz; kesin model invocation sayısı iddia
edilmez. A2.1'de runtime işlemleri **NOT RUN** idi; sonraki A2.2 kanıtı yukarıdaki
checkpoint'tedir. A2.3'te static validation ve 34 offline test yeniden geçti;
runtime/SQL veya önceki 119/119 suite ve 12/12 PG testleri yeniden çalıştırılmadı.
