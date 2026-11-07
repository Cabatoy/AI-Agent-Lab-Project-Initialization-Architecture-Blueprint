# AI Agent Lab — Faz + Epik + Task Roadmap

> Tarihsel yol haritası: kaynak yolları yeni yerleşimi gösterir; öneriler onay veya güncel durum beyanı değildir. Bkz. `docs-md/continuation.md`.

## Yönetici Özeti

Bu roadmap, mevcut local prototipi ürün seviyesine taşımak için uygulanabilir bir yürütme planıdır.

Yaklaşım:
- Mevcut çalışan çekirdeği koru (Control Plane + Worker + Redis/PostgreSQL)
- Üstüne orkestrasyon, görev bağımlılığı, çok ajanlı yürütme ve yönetişim katmanlarını kademeli ekle
- Her faz sonunda çalışan dikey dilim, ölçülebilir kabul kriteri ve onay kapısı uygula

---

## Teknik Ek

## Güncel İlerleme Notu (Uygulandı)

- `src/ControlPlane/Program.cs` içinde task state modeli genişletildi.
- Merkezi geçiş doğrulaması (`TaskStatusTransitions`) eklendi.
- `approve/retry/status` endpointleri merkezi transition kuralına bağlandı.
- `task_status_transitions` geçmiş tablosu ve kayıt fonksiyonu eklendi.
- Retry akışı `Failed -> Retrying` olacak şekilde güncellendi.
- `src/AgentWorker/Program.cs` içinde transient/permanent hata sınıflandırması, status update retry ve in-memory idempotency guard uygulandı.
- Worker tarafında dead-letter queue baseline eklendi (`TASK_DEAD_LETTER_QUEUE_KEY`, `DeadLetterMessage`, terminal failure path’lerinde enqueue).
- `tests/AgentWorker.Tests` projesi oluşturuldu, solution’a eklendi ve Worker reliability testleri (12 test) başarıyla geçti.
- `tests/ControlPlane.Api.Tests` projesi eklendi; status transition kuralları için 11 test yazıldı.
- Faz 2 için planlama katmanı iskeleti eklendi: `/plans`, `/plans/generate`, `/plans/{id}/approve` endpointleri ve `execution_plans` şeması.
- Transaction kullanan endpoint’lerde bağlantı açık başlatma düzeltmesi yapıldı (`await connection.OpenAsync()`), bootstrap smoke testindeki 500 hatası giderildi.
- Regresyon doğrulaması tamamlandı: solution build başarılı, toplam 23 test geçti.
- `scripts/local.ps1 bootstrap-dev -LlmProvider podman-ai-lab` uçtan uca çalıştırıldı; stack ayağa kalktı, smoke test `Completed` oldu, runtime model ayarları `podman-ai-lab` ile doğrulandı.

## Faz 1 — Foundation Hardening

### Epik 1.1: Domain ve State Machine Genişletme
- Task 1.1.1: Task status modelini genişlet (`Proposed, Planned, InProgress, Review, Blocked, Cancelled, Rejected`)
- Task 1.1.2: Geçiş kuralları için merkezi validator yaz
- Task 1.1.3: `TaskTransitionHistory` tablosu ekle
- Task 1.1.4: API response’larına transition metadata ekle

### Epik 1.2: Güvenilirlik ve Hata Yönetimi
- Task 1.2.1: Worker retry politikasını sınıflandır (transient/permanent)
- Task 1.2.2: Idempotency koruması ekle (task duplicate processing)
- Task 1.2.3: Dead-letter queue yaklaşımı tasarla
- Task 1.2.4: Status update failure için kompanzasyon akışı ekle

### Epik 1.3: Test Temeli
- Task 1.3.1: Control Plane API integration test projesi ekle
- Task 1.3.2: Worker unit testleri (prompt build, retry, parsing)
- Task 1.3.3: Minimal local end-to-end smoke test scriptini standardize et

**Faz 1 Kabul Kriterleri**
- Genişletilmiş state geçişleri kural dışı durumda 400 döndürür
- Worker aynı task’ı iki kez tamamlayamaz
- Kritik akış testleri CI olmadan local’de deterministik çalışır

---

## Faz 2 — Orchestrator ve Planlama Katmanı

### Epik 2.1: Talep -> Plan Dönüşümü
- Task 2.1.1: `ProjectRequest` ve `ExecutionPlan` veri modellerini ekle
- Task 2.1.2: Plan oluşturma endpoint’i (`/plans/generate`) tanımla
- Task 2.1.3: Plan onay endpoint’i (`/plans/{id}/approve`) ekle

### Epik 2.2: Görev Bağımlılığı Motoru
- Task 2.2.1: Task dependency graph modeli ekle
- Task 2.2.2: Topological order resolver yaz
- Task 2.2.3: Blocked task çözümleyici kuralını uygula

### Epik 2.3: Onay Kapıları
- Task 2.3.1: İnsan onayı gerektiren geçişleri policy ile işaretle
- Task 2.3.2: Approval log kaydı ekle
- Task 2.3.3: Onay reddi ve yeniden planlama akışı ekle

**Faz 2 Kabul Kriterleri**
- Talep plan’a dönüşür, onay olmadan execution başlamaz
- Bağımlı görevler doğru sırayla çalışır
- Reddedilen planlar audit kaydına düşer

---

## Faz 3 — Agent Workforce

### Epik 3.1: Agent Definition/Instance Ayrımı
- Task 3.1.1: AgentDefinition şeması (rol, yetki, model policy)
- Task 3.1.2: AgentInstance lifecycle yönetimi
- Task 3.1.3: Capability matching algoritması

### Epik 3.2: Görev Atama ve İzlenebilirlik
- Task 3.2.1: Task -> Agent assignment policy
- Task 3.2.2: Agent run history kaydı
- Task 3.2.3: Agent performans metrikleri (başarı, süre, retry)

### Epik 3.3: Güvenlik Sınırları
- Task 3.3.1: Tool permission allowlist
- Task 3.3.2: Repo/path scope kısıtı
- Task 3.3.3: Secrets redaction doğrulaması

**Faz 3 Kabul Kriterleri**
- En az 3 uzman rol ile görev ataması çalışır
- Yetkisiz araç çağrısı engellenir
- Agent run geçmişi sorgulanabilir olur

---

## Faz 4 — Ürün Arayüzü ve Yönetişim

### Epik 4.1: Chat + Dashboard
- Task 4.1.1: Conversation -> Request mapping
- Task 4.1.2: Plan/Task timeline görünümü
- Task 4.1.3: Onay/ret aksiyon ekranı

### Epik 4.2: Audit ve Operasyon Görünürlüğü
- Task 4.2.1: AuditEvent modeli ve API
- Task 4.2.2: Maliyet etiketleme (project/task/model)
- Task 4.2.3: Hata ve olay raporlama paneli

### Epik 4.3: Kalite Kapıları
- Task 4.3.1: PR öncesi test gate
- Task 4.3.2: Güvenlik policy gate
- Task 4.3.3: Doküman güncelleme onay kapısı (`docs-md` -> `docs` PDF)

**Faz 4 Kabul Kriterleri**
- Ürün sahibi planı görür/onaylar/reddeder
- Audit trail kesintisiz tutulur
- Kalite kapısı geçmeden finalize işlemi yapılamaz

---

## Fazlar Arası İlişkiler

- Faz 1 tamamlanmadan Faz 2 üretimsel risk taşır
- Faz 2 tamamlanmadan Faz 3 çok ajanlı akış hataya açıktır
- Faz 3 tamamlanmadan Faz 4 gerçek yönetişim sağlayamaz

Kritik bağımlılık zinciri:
`State machine -> Plan graph -> Agent assignment -> Governance UI`

---

## 2 Haftalık Sprint Önerisi (Özet)

- Sprint 1-2: Faz 1
- Sprint 3-4: Faz 2
- Sprint 5-6: Faz 3
- Sprint 7-8: Faz 4

Her sprint sonunda:
1. Çalışan demo
2. Test kanıtı
3. Doküman güncelleme onayı sorusu
4. Onaylıysa `docs-md` + `docs` PDF senkron güncelleme
