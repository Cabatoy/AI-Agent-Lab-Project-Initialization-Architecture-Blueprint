# HIC Roadmap and Prioritized Backlog

## Yönetici Özeti

Phase 0, mevcut AI Agent Lab'ın yeteneklerini doğrulayıp HIC'in sınırlarını belgelemektir. Sonraki geliştirme; mevcut task engine'i yeniden yazmadan, önce runtime uyumluluğu ve durable recovery risklerini ele almalı, ardından plan dependency graph'ı ve scheduling'i eklemelidir.

Bu roadmap önerilen sıradır; Phase 1 veya sonraki işler için uygulama onayı değildir. Her iş öncesinde kapsam, veri etkisi ve kabul ölçütleri onaylanmalıdır.

## Teknik Ek

### Phase 0 — Mimari temel (bu teslim)

- Beş HIC mimari belgesi hazırlanır.
- Mevcut ve önerilen işlevler açıkça ayrılır.
- Kaynak kodu veya altyapı değişikliği yapılmaz.

### Phase 1+ — Öncelikli backlog

#### P0 — .NET runtime migration değerlendirmesi (feature önkoşulu)

- **Bağımlılıklar:** Yok.
- **Beklenen değişiklikler:** Önce uyumluluk değerlendirmesi; karar geçişi gerektirirse ayrı onaylı runtime/SDK/container/CI güncellemesi. Bu belgenin kendisi migration yapmaz.
- **Kabul ölçütleri:** .NET 8→10 geçiş kararı; destek ve güvenlik durumu, paketler, build/test, local Podman ve CI etkisi kayıtlı; geçiş gerekiyorsa geri dönüş yaklaşımı belirli. Yeni feature çalışması değerlendirme kararı çıkana kadar başlamaz.
- **Gerekli testler:** Solution build/test, her projede hedef framework doğrulaması, container build ve local smoke test; uygun CI doğrulaması.

#### P1 — Durable execution recovery ve Redis/PostgreSQL tutarlılığı

- **Bağımlılıklar:** P0 değerlendirmesi; mevcut task lifecycle'ının failure-mode analizi.
- **Beklenen değişiklikler:** Onaylanırsa PostgreSQL outbox/dispatch kaydı, güvenli publish/retry, Worker claim/lease ve recovery, idempotency/concurrency kontrolü. Uygulama şekli analiz sonrasında seçilir.
- **Kabul ölçütleri:** Queue teslimi kaybolmaz veya güvenle yeniden oluşturulur; duplicate teslim aynı task etkisini ikinci kez üretmez; Worker/Redis/API kesintisi sonrası task audit edilebilir biçimde sürer.
- **Gerekli testler:** PostgreSQL commit sonrası publisher kapanması, duplicate message, Worker crash/restart, lease expiry, Redis ve Control Plane kesintisi, DLQ/replay ve eşzamanlı worker entegrasyon testleri.

#### P2 — Dependency graph model ve validasyonu

- **Bağımlılıklar:** P1 için kararlaştırılmış task/dispatch consistency; plan ve task kimliklerinin sahiplik kararı.
- **Beklenen değişiklikler:** Mevcut task kayıtlarıyla ilişkilendirilen plan içi dependency ilişkileri ve API validasyonu. İkinci task tablosu/state machine yaratılmaz; migration/rollback tasarımı eklenir.
- **Kabul ölçütleri:** Edge yönü önkoşuldan bağımlıya; self-edge, duplicate edge, bulunmayan task, plan dışı edge ve cycle reddedilir; onay belirli graph sürümüne bağlıdır.
- **Gerekli testler:** DAG, cycle, self-edge, eksik/yanlış plan task'ı, duplicate edge; migration ve transaction testleri.

#### P3 — Topolojik scheduling

- **Bağımlılıklar:** P2.
- **Beklenen değişiklikler:** Mevcut task engine'e delege eden dependency coordinator; hazır task seçme ve kararlı sıralama.
- **Kabul ölçütleri:** Yalnızca insan tarafından onaylanmış graph'taki ve bütün önkoşulları `Completed` olan task'lar kuyruğa girer; hazır task seçimi deterministiktir; yeniden başlatma sonrası aynı graph sürümü güvenle devam eder.
- **Gerekli testler:** Topolojik sıralama, bağımsız eşzamanlı task'lar için deterministik tie-break, henüz hazır olmayan task, graph onay sürümü değişikliği, tekrar dispatch ve failure recovery.

#### P4 — Blocked-task çözümü

- **Bağımlılıklar:** P2 ve P3.
- **Beklenen değişiklikler:** Blocker nedeni/referansı, dependency durumuna göre yeniden değerlendirme ve mevcut state machine üzerinden resume.
- **Kabul ölçütleri:** Önkoşullar tamamlanmadan task çalışmaz; blocker kalkınca task yalnızca gerekli onay/policy kontrolleri hâlâ geçerliyse hazır olur. Başarısız/iptal/reddedilmiş önkoşul sessizce başarı sayılmaz.
- **Gerekli testler:** Blocked→ready, blocker sürerken tekrar değerlendirme, başarısız/iptal önkoşul, planın reddi ve audit/resume testleri.

#### P5 — Plan onay, ret ve replanning akışları

- **Bağımlılıklar:** P2 plan sürümü; insan kimliği/audit sözleşmesi ve mevcut task transition rules.
- **Beklenen değişiklikler:** Plan sürüm kaydı, onay/ret aktörü ve gerekçesi, ret endpoint'i, yeni sürüm/replan akışı. Mevcut task state machine korunur.
- **Kabul ölçütleri:** Onay yalnızca değişmeyen plan sürümüne uygulanır; ret audit edilir; yeni plan onay alınmadan task yürütmeye açılmaz; hassas eylemler açık onay gerektirir.
- **Gerekli testler:** Geçerli/geçersiz geçiş, aynı istek tekrarı, eski sürüm onayı, ret gerekçesi, audit kaydı ve onaysız dispatch'in reddi.

#### P6 — Provider-neutral execution contract

- **Bağımlılıklar:** P1 correlation/idempotency yaklaşımı; P0 runtime kararı.
- **Beklenen değişiklikler:** Sürümlü provider/agent request-result sözleşmeleri, hata sınıfları, cancellation, budget ve artefakt referansları. Mevcut Worker'ı aşamalı biçimde bu sözleşmenin tüketicisi yap.
- **Kabul ölçütleri:** Orchestration provider'a özgü payload veya isim bilmez; capability ve sürüm açıkça belirtilir; içerik/sırlar loglara yazılmaz.
- **Gerekli testler:** JSON contract round-trip, zorunlu alan/unsupported version, cancellation, error mapping, idempotency/correlation ve redaction.

#### P7 — Deterministic-first, local-first model routing

- **Bağımlılıklar:** P6.
- **Beklenen değişiklikler:** Model gereksinimi/capability sınıflandırması, izinli local adapter seçimi ve açık policy ile dış provider seçimi.
- **Kabul ölçütleri:** Deterministik işler LLM kullanmaz; yeterli izinli local model önceliklidir; local hata/kapasite aşımı sessiz cloud fallback başlatmaz; ücretli/external route açık policy/onay olmadan seçilmez.
- **Gerekli testler:** Model gerekmeyen task, local route, yetersiz capability, timeout, provider unavailable, explicit cloud authorization ve no-fallback davranışı.

#### P8 — Token ve maliyet telemetrisi

- **Bağımlılıklar:** P6 contract usage alanları; P7 routing kimliği.
- **Beklenen değişiklikler:** Task/run/provider/model bazlı usage, fiyatlandırma sürümü ve ölçüm güven düzeyi kayıtları; raporlama ve redaction.
- **Kabul ölçütleri:** Sağlayıcı gerçek token sayısı veriyorsa actual; aksi halde estimated/unknown ayrımı korunur. Retry maliyeti görünür; ham prompt, secret veya chain-of-thought saklanmaz. Bütçe aşımı davranışı politika ile tanımlanır.
- **Gerekli testler:** Usage aktarımı, eksik ölçüm, retry toplamı, fiyat sürümü, redaction ve budget eşik/uyarı testleri.

#### P9 — Minimal HIC control dashboard

- **Bağımlılıklar:** P5 approval API/audit; task/plan görünüm API'leri.
- **Beklenen değişiklikler:** Talep/plan/task listesi, bağımlılık durumu, onay/ret, blocker ve audit görünümü.
- **Kabul ölçütleri:** Kullanıcı plan sürümünü ve etkisini görür; hassas aksiyonlar açık onay ister; UI mevcut Control Plane sözleşmesini kullanır ve yeni task lifecycle yaratmaz.
- **Gerekli testler:** UI/API contract, rol/izin, onay/ret, stale plan, blocker ve error state testleri.

#### P10 — İlk repository generation workflow

- **Bağımlılıklar:** P1–P8'in güvenli dikey dilim için gerekli kısımları; izolasyon ve repo izin politikası.
- **Beklenen değişiklikler:** Brief→plan→insan onayı→izole repo workspace→ajan işi→build/test→PR-ready artefakt workflow'u. GitHub/CI entegrasyonu ayrıca onaylanır.
- **Kabul ölçütleri:** Ajan yalnızca izinli branch/repo ve araçları kullanır; iş durumu resume/audit edilebilir; PR/merge ve production deployment kapıları insanda kalır.
- **Gerekli testler:** Uçtan uca başarı, test başarısızlığı, retry/resume, izin ihlali, cancellation, provider failure, token/cost raporu ve insan onayı testleri.

### Bağımlılık özeti

```text
P0 -> P1 -> P2 -> P3 -> P4
                  \-> P5
P0 + P1 -> P6 -> P7 -> P8
P5 -> P9
P1 + P2 + P5 + P6/P7/P8'in gerekli dilimleri -> P10
```

Bağımlılıkların tam kapsamı uygulama tasarımında teyit edilir; özellikle P4 ve P5, scheduling davranışıyla birlikte değerlendirilmelidir.
