# HIC Architecture

## Yönetici Özeti

HIC, AI Agent Lab'ın mevcut Control Plane ve Worker yürütme omurgasını genişleten bir governance, interaction ve product layer olarak tasarlanır. Tek task engine ve tek task state machine korunur. HIC; talepleri planlama, bağımlılıkları koordine etme, insan kararlarını kaydetme ve işi görünür kılma sorumluluğunu üstlenir.

.NET 10 migration ve R1 atomik task geçişleri tamamlandı; önceki checkpoint'te Release build ve 77/77 test geçti. R1 container E2E hâlâ bekliyor. Redis/PostgreSQL tutarlılığı ve durable recovery, dependency scheduling öncesindeki yüksek öncelikli risklerdir. Track B Agent Bridge, bunlara paralel read-only erişim ve sözleşme tasarımı yapar; execution yetkisini devralmaz.

Bu mimari; AWS altyapısı kurmaz, üretim runtime'ı veya model sağlayıcısı seçmez. Local Podman prototipi geliştirme ve doğrulama seçeneğidir; uzun vadeli çalışma kişisel cihaza veya ev ağına bağımlı olamaz. AWS hedefi korunur; somut AWS tasarımı ve provisioning sonraki onaylı fazların konusudur.

## Teknik Ek

### 1. Sınırlar ve sahiplik

| Alan | Sorumluluk | Durum |
|---|---|---|
| AI Agent Lab Control Plane | Task API'si, mevcut task durum geçiş kuralları, plan iskeleti API'si | Kısmen uygulanmış |
| AI Agent Lab Worker | Kuyruktan task alma, model isteği, durum yazma, sınırlı retry ve DLQ | Prototipte uygulanmış |
| AI Agent Lab data/queue | PostgreSQL kayıtları ve Redis tabanlı kuyruk | Local prototipte uygulanmış |
| HIC governance | Plan sürümü, onay/ret, aktör ve gerekçe, politika ve audit görünümü | Önerilen; uygulanmamış |
| HIC dependency coordination | Plan içi task grafiği ve hazır task belirleme; aynı task engine'e delege etme | Önerilen; uygulanmamış |
| HIC product/interaction | Talep, plan ve yürütme durumunun kullanıcıya gösterilmesi | Önerilen; dashboard yok |
| HIC Agent Bridge | Sürümlü, yetkili ve kompakt status/result/proposal adaptasyonu | B0 tasarımı; kod/MCP server yok |
| Execution isolation | Görev kapsamlı workspace, izin ve geçici credential sınırları | Hedef; mevcut Worker tarafından garanti edilmez |
| Cloud runtime | Kişisel cihazdan bağımsız çalışma ve gerektiğinde bulut execution | Gelecek hedef; AWS provisioning yok |

HIC kendi içinde ikinci bir task kaydı veya state machine oluşturmamalıdır. Plan, task ve yürütme arasındaki ilişki mevcut task kayıtları ve onaylı API/sözleşmeler üzerinden kurulmalıdır. Yeni tablo veya servis gerekirse var olan engine'in verisini aynalamak yerine onunla tutarlı, tekil sahiplik sağlayacak şekilde tasarlanmalıdır.

### 2. Önerilen mantıksal akış

```text
Kullanıcı
  -> HIC Interaction / Dashboard
  -> HIC Governance: talep, plan sürümü, onay ve politika
  -> AI Agent Lab Control Plane: mevcut task kayıtları ve geçişleri
  -> Dependency Coordinator: hazır task seçimi
  -> Mevcut Redis queue ve Worker yürütmesi
  -> Model/runtime adapter (opsiyonel)
  -> Sonuç, audit ve ölçüm -> Control Plane / HIC görünümü
```

Dependency Coordinator bir ikinci task engine değildir. Plan grafiğine göre hangi mevcut task'ın hazır olduğunu belirler; onaylı task'ı mevcut Control Plane/queue yolu üzerinden yürütmeye verir. Durumun tek kaynağı mevcut task lifecycle'ı olmalıdır.

### 3. Önerilen dependency graph ve scheduling

Bu tasarım henüz uygulanmış değildir; Phase 1 öncesi sözleşme ve veri sahipliği netleştirilmelidir.

- Graph, tek bir execution plan içindeki task düğümlerinden oluşur. Kenar yönü `önkoşul -> bağımlı task` şeklindedir.
- Dependency kaydı aynı planın iki geçerli task'ını referanslamalıdır. Self-edge, yinelenen edge, bulunmayan task, planlar arası edge ve cycle reddedilir.
- Plan onayı graph'ın belirli sürümüne bağlanır. Onaydan sonra graph değişirse eski onay yeni sürümü kapsamaz; tekrar insan onayı gerekir.
- Scheduling deterministiktir: yalnızca onaylı planın, tüm önkoşulları `Completed` olan ve mevcut politika/kapasite kontrollerini geçen task'ları hazırdır. Birden fazla task aynı anda hazırsa kararlı bir sıra kuralı (plan sıra numarası, ardından task ID gibi) uygulanır.
- Scheduler task'ı mevcut task engine'e delege eder; Worker doğrudan graph'ı yorumlayarak alternatif bir lifecycle yaratmaz.
- `Blocked`, bağımlılıklar tamamlanana kadar yürütülmeyecek task'ları temsil etmek için kullanılabilir; ancak mevcut kodda gerçek blocker tespiti ve otomatik çözüm yoktur. Blocker nedeni ve bağlı olduğu task'lar kaydedilmelidir. Önkoşullar tamamlanınca task yeniden değerlendirilir; sırf bağımlılık tamamlandı diye insan onayı gereken task otomatik onaylanmaz.
- Önkoşul başarısız, reddedilmiş veya iptal edilmişse aşağı akış task'ların davranışı (blocked kalma, iptal, yeniden planlama) açık policy ile belirlenmelidir; sessizce çalıştırılmaz.
- Aynı task'ın iki Worker tarafından sahiplenilmesi, queue'dan çıkarma ile PostgreSQL güncellemesi arasındaki çökme aralığı ve yeniden başlatma sonrası recovery ayrıca ele alınır. Process içi in-flight guard bunun yerine geçmez.

### 4. Durable execution ve veri tutarlılığı

Redis queue pop ve PostgreSQL state update tek atomik işlem değildir. Worker'ın task'ı aldıktan hemen sonra kapanması, task'ın kaybolmasına; durum güncellemesinin kaybolması ise yeniden teslimde tutarsızlığa yol açabilir. Bu, dependency scheduling öncesi yüksek öncelikli güvenilirlik konusudur.

Değerlendirilecek tasarım unsurları:

1. PostgreSQL'de kalıcı dispatch/outbox kaydı ve transaction içinde task state ile birlikte yazma.
2. Outbox publisher'ın Redis'e tekrar güvenle gönderebilmesi; tekrarlı teslimin idempotency key ile etkisiz olması.
3. Worker claim/lease, lease süresi, heartbeat ve süresi dolan claim'lerin recovery akışı.
4. Task status ve attempt sayısı için eşzamanlılık kontrolü; eski event'in daha yeni durumu ezmesini engelleme.
5. Kuyruk mesajı ile execution state'in korelasyon/audit bilgisi.
6. Retry, DLQ replay ve manuel resume semantiği ile PostgreSQL/Redis kesintilerinin test edilmesi.

Outbox veya lease belirli bir uygulama kararı olarak henüz onaylanmamıştır. Önce failure mode ve kabul kriterleri tanımlanmalı, sonra en küçük güvenilir çözüm seçilmelidir.

R1, task state ve audit'i tek kilitli transaction'da yazar; aynı-status/expected-state uyuşmazlığı `409` üretir. Bu, execution ownership değildir. Yönelim PostgreSQL'i authoritative state, Redis'i bildirim/dispatch mekanizması tutmaktır. A2 ownership/fencing, A3 lease/heartbeat ve A4 reconciliation uygulama sözleşmeleri ayrıca onaylanmalıdır; outbox zorunlu seçilmiş değildir. Bridge bunlardan önce distributed exactly-once veya güvenli otomatik recovery iddia edemez.

### 5. İnsan onay sınırları

İnsan onayı, yalnızca arayüzdeki bir düğme değil; sürümü, kararı veren aktörü, zamanı ve gerekçeyi bağlayan kalıcı bir governance kaydı olmalıdır.

- **Talep ve plan:** Task'ları yürütmeye açmadan önce insan kapsamı, kabul ölçütlerini ve plan sürümünü onaylar.
- **Hassas eylemler:** Yetki kapsamını genişletme, sır/credential kullanımı, dış sisteme veri gönderme, ücretli/cloud kaynak veya harcama sınırını değiştirme, geri alınması zor değişiklik ve production etkisi açık onay gerektirir.
- **Kod değişiklikleri:** Ajan çıktısı güvenilmezdir; test ve uygun güvenlik kontrolleri uygulanır. PR hazırlama/inceleme akışı insan denetimine açıktır; merge politikası ayrıca belirlenir.
- **Production:** Dağıtım için her durumda açık insan onayı zorunludur.
- **Ret ve yeniden planlama:** Ret, gerekçe ve reddedilen plan sürümüyle audit edilir. Düzeltilmiş plan yeni sürümdür ve yürütme öncesi yeniden onaylanır.
- **Devam ve retry:** Otomatik retry yalnızca önceden onaylanmış sınırlar içinde yapılır. İçerik, yetki veya maliyet kapsamı değişiyorsa insan kararı gerekir.
- **Duraklatma/iptal:** Kullanıcı işi durdurabilmeli veya iptal edebilmeli; devam kararı kalıcı task/workflow kaydından alınmalıdır.

Mevcut task approve endpoint'i aktör kimliği, plan sürümü veya onay gerekçesi kaydettiğini göstermemektedir; bu özellik varmış gibi kabul edilmemelidir.

### 6. Provider-neutral, deterministic-first ve local-first

- Graph doğrulama, policy, onay denetimi, hazır task hesaplama, idempotency ve format kontrolleri deterministik kodla yapılır; LLM bunların karar kaynağı olmaz.
- Model gerekmeyen task'larda model çağrısı yapılmaz.
- Model gereken işte önce veri sınıflandırması, görev yeteneği ve kalite gereksinimi kontrol edilir; izinli ve yeterli yerel model varsa local route tercih edilir.
- Yerel model yoksa veya başarısızsa cloud modele otomatik/sessiz fallback yapılmaz. Dış sağlayıcı kullanımı politika ve insan onayına tabi olmalıdır.
- Provider adapter'ları ortak ve sürümlü execution contract'ı uygular. Routing policy ile sağlayıcı seçimi adapter implementasyonundan ayrılır.
- `podman-ai-lab`, `ollama` ve `openai-compatible` bugün aynı istemci uygulamasını kullanır; bunları bağımsız, yetenekleri doğrulanmış adapter'lar gibi belgelemeyin.
- Production model sağlayıcısı, cloud çalışma zamanı ve bulut execution politikası bu fazda seçilmez.

### 7. Runtime ve cloud konuları

.NET 10 migration commit'i `699bd116bf49cbef0e8ac97d791f1b02b4c51305` ile kayıtlıdır. R1 DB integration ve build/test checkpoint'i [devam belgesinde](../../docs-md/continuation.md) ayrıştırılır; yeni container sürümünün çalışması önceki image E2E'sinden çıkarılmaz. Repo içinde Actions workflow bulunmaması CI doğrulaması yapıldığı anlamına gelmez.

Local Podman stack'i geliştirici doğrulama ortamıdır; cloud runtime'ın zorunlu bileşeni olamaz. AWS seçimi mevcut proje kararlarında yer alır ancak AWS kaynakları bu fazda provision edilmez. Hedef hibrit yapıda Control Plane/workflow durumu kişisel cihazdan bağımsız kalıcı serviste; execution ise politika ve veri sınıfına göre yerel veya izole cloud ortamında çalışabilir. AWS servis eşlemesi, maliyet, veri konumu ve işletim sorumluluğu ayrıca onaylanmalıdır.

### 8. Paralel Agent Bridge sınırı

[Agent Bridge](./AGENT_BRIDGE.md), dış ajan/interaction kanalını mevcut Control Plane'e bağlayan ince anti-corruption adapter'dır:

```text
GitHub / ileride MCP veya onaylı agent client
  -> Bridge: principal + project scope + contract + redaction
  -> Control Plane: status/result veya yetkili proposal/approval akışı
  -> mevcut Redis/Worker -> sonuç referansı -> aynı Bridge
```

Bridge ayrı queue, scheduler veya task state tutmaz. Kanal teslim/audit/idempotency metadata'sı task lifecycle değildir. B1 yalnızca okur; B3/B4 için kalıcı insan onayı, proje kimliği/yetkilendirmesi ve Track A execution güvenliği gerekir. Ajanın `approved: true` beyanı execution izni değildir. Local endpoint'i ChatGPT erişsin diye public açmak B0 kararı değildir.
