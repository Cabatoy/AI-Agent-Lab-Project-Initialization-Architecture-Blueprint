# AI Agent Lab — Birleşik Master Doküman (TR)

> Tarihsel özet: kaynak yolları yeni yerleşimi gösterir; teknik durum ve önerilen adımlar güncel onay anlamına gelmez. Bkz. `docs-md/continuation.md`.

## Yönetici Özeti

Bu master doküman, üç ayrı perspektifi tek metinde toplar:
1. Mevcut mimari değerlendirme,
2. Proje vizyonu ve yol haritası hikayesi,
3. Uygulamaya geçişte yönetimsel karar çerçevesi.

Amaç, teknik ekibin ve karar vericilerin aynı resmi görmesini sağlamaktır.

---

## Bölüm A — Bugün Ne Var?

AI Agent Lab deposunda bugün çalışan sistem, local prototip düzeyinde bir görev yürütme çekirdeğidir.

**Gerçeklenenler:**
- .NET 8 Control Plane API
- .NET 8 Worker (`BackgroundService`)
- PostgreSQL + Redis tabanlı task işleme
- Podman lifecycle scriptleriyle tekrarlanabilir local kurulum

**Sınırlar:**
- Çok ajanlı orkestrasyon henüz başlangıç seviyesinde
- Chat ürün katmanı henüz yok
- CI/test altyapısı net değil
- Gelişmiş görev bağımlılık yönetimi ve governance katmanı eksik

---

## Bölüm B — Neye Evriliyor?

Hedef ürün, doğal dilden yazılım geliştirme süreci yöneten bir platformdur:
- talep analizi,
- görev planlama,
- ajan atama,
- güvenli yürütme,
- test/doğrulama,
- PR ve insan onayı.

Bu dönüşüm, tek adımda değil, kontrollü fazlarla yapılmalıdır.

---

## Bölüm C — MVP’den Ürüne Yol Haritası

1. **Çekirdeği sertleştir** (state model + test)
2. **Orkestratörü ekle** (planlama + bağımlılık)
3. **Agent workforce kur** (role/instance ayrımı)
4. **Ürün arayüzünü aç** (chat + dashboard)
5. **Cloud geçişine hazırlan** (provider bağımsızlık + maliyet görünürlüğü)

---

## Teknik Ek

## Teknik referanslar
- API: `src/ControlPlane/Program.cs`
- Worker: `src/AgentWorker/Program.cs`
- Compose: `docker-compose.yml`
- Scriptler: `scripts/local.ps1`, `scripts/local.sh`
- Rehber: `tools/run-me/Program.cs`
- Mimari belgeler: `docs-md/00..06`, `docs-md/decisions/*`

## Önerilen temel domain genişlemeleri
- Project
- TaskDependency
- Approval
- AgentDefinition / AgentInstance
- ExecutionRun / Artifact / ValidationResult
- AuditEvent

## Güvenlik ve yönetişim çerçevesi
- İnsan onayı olmayan kritik publish/deploy yok
- Sırlar log/prompt/repo’ya yazılmaz
- Görev araçları izin listesiyle sınırlandırılır
- Sağlayıcı seçimi ve geçişi policy ile yönetilir

---

## Sonraki Belgeye Geçiş Notu

Takip eden ayrı doküman; local development operasyon standardını, faz-2 cloud geçişini (AWS/Azure ve benzeri), barındırma + aylık bakım maliyet modelini ve sağlayıcılar arası yeniden değerlendirme/göç kriterlerini yönetici özeti + teknik ek formatında detaylandıracaktır.
