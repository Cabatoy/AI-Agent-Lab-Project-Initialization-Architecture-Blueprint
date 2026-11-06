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

## Teknik Ek — Gelecekteki komutlar

**Henüz çalıştırılmadı.** Runtime onayı alındıktan sonra Windows PowerShell'de:

```powershell
$project = 'hic-net10-' + ([guid]::NewGuid().ToString('N').Substring(0, 12))
$port = 18080
$script = '.\deploy\hic-runtime-test\runtime-test.ps1'

& $script validate
& $script preflight -Project $project -ApiPort $port
& $script start -Project $project -ApiPort $port -ApproveRuntime
& $script readiness -Project $project -ApiPort $port
& $script persistence -Project $project -ApiPort $port -ApproveRuntime
& $script worker -Project $project -ApiPort $port -ApproveRuntime
& $script stop -Project $project -ApiPort $port -ApproveRuntime
```

Her adım başarısızsa sonraki adıma geçmeyin. `-ApproveRuntime`, ayrı insan onayının yerine geçmez; onaylandıktan sonra açık operasyon kapısıdır. `preflight`/`readiness` de Podman ve readiness için API çağrısı yaptığından ayrıca çalışma izni gerektirir.

### Beklenen sonuçlar

| Komut | Davranış / kanıt |
|---|---|
| validate | JSON/paths, ownership label, port, model endpoint ve queue konfigürasyonunu statik doğrular |
| preflight | Podman bağlantısını, var olan proje kaynaklarının sahipliğini ve boş host portunu denetler; servis başlatmaz |
| start | Projeye özel konfigürasyon üretir; yalnızca DB/Redis/stub/API build/start yapar; Worker başlamaz |
| readiness | Container ownership, dependency health, PostgreSQL `SELECT 1`, Redis PING ve API health yanıtını doğrular |
| persistence | Yeni Draft task oluşturur; UUID'yi kaydeder; PostgreSQL status ve API read-back doğrular |
| worker | Aynı Draft task'ı onaylar; kuyrukta tam doğru UUID'yi kontrol eder; Worker'ı başlatır; Completed/sabit çıktı/attempt 1/audit/boş queue ve DLQ bekler |
| stop | Yalnızca doğrulanmış test container ID'lerini 30 saniye grace süresiyle durdurur; volume/network/container/state silmez |

### Veri etkisi, rollback ve sınırlar

`start` yeni DB şeması, named volume, ağ ve image oluşturur; `persistence`/`worker` test DB ve queue'ya yazar. Önceki test verileri otomatik temizlenmez. Aynı state'te ikinci task veya otomatik retry yoktur; başarısız bir test inceleme için korunur. Yeni deneme için benzersiz yeni proje kullanın.

Rollback yalnızca `stop` ile test container'larını durdurmaktır. Kalıcı test verileri ve image'lar kalır; silme ayrı onay gerektirir. Machine, üretim container'ları, `.env`, PostgreSQL volume'ları ve AI Lab ağ bağlantıları değiştirilmez.

Bu sürüm yalnızca başarı yolu ve doğrudan DB/API read-back doğrular; container restart sonrası persistence, fault injection, retry/crash recovery, aktif iş sırasında cancellation, gerçek LLM ve Swagger/TLS/globalization kontrolleri uygulanmaz. Worker durdurma sırasında aktif işin recovery garantisi yoktur. Compose provider label/name/health ve internal network davranışı gerçek runtime'da ayrıca doğrulanmalıdır. Port bind başarısızlığı güvenli hata olarak durdurulur; localhost başarısızsa başka IP'ye fallback yapılmaz.
