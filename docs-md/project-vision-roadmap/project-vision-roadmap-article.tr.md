# AI Agent Lab — Proje Taslağı ve Yolculuk Hikayesi (TR)

## Yönetici Özeti

AI Agent Lab’ın amacı, “kod üreten bir sohbet botu” olmak değil; ürün sahibinin doğal dilde verdiği hedefleri, denetlenebilir yazılım geliştirme iş akışlarına dönüştüren bir platform olmaktır.

Bu makale, projeyi bir ürün hikayesi olarak anlatır:
- Nereden başladık? (local prototip)
- Nereye gidiyoruz? (çok ajanlı orkestrasyon platformu)
- Hangi aşamalardan geçeceğiz? (MVP odaklı fazlar)

Ana yaklaşım: **önce çalışır çekirdek, sonra kontrollü genişleme**.

---

## Hikaye: “Tek görevden platforma”

Başlangıçta elimizde küçük ama değerli bir çekirdek var:
- Bir görev oluşturuluyor,
- Onaylanıyor,
- Worker tarafından işleniyor,
- Sonuç geri yazılıyor.

Bu akış tek başına “platform” değil; fakat platformun kalbi olabilecek bir yürütme iskeletidir. Bundan sonra yapılacak iş, bu iskeletin etrafına ürün kabiliyetleri eklemektir:
- Orkestrasyon,
- Çok ajanlı görev paylaşımı,
- İnsan onayı,
- Kalite kapıları,
- İzlenebilirlik ve maliyet yönetimi.

---

## Fazlar (önerilen anlatım)

## Faz 1 — Çalışan Çekirdeğin Sertleştirilmesi

Hedef:
- Task modelini genişletmek,
- Geçiş kurallarını netleştirmek,
- Test temeli atmak.

Çıktı:
- Daha güvenilir ve ölçülebilir local yürütme çekirdeği.

## Faz 2 — Orkestrasyon Katmanı

Hedef:
- Kullanıcı talebini planlara çeviren orchestrator,
- Görev bağımlılıkları ve onay akışları.

Çıktı:
- “Talep -> Plan -> Onay -> Yürütme” zincirinin görünür hale gelmesi.

## Faz 3 — Agent Workforce

Hedef:
- Agent Definition / Agent Instance ayrımı,
- Uzman rollere göre görev atama.

Çıktı:
- Tek worker mantığından çok ajanlı iş bölüşümüne geçiş.

## Faz 4 — Ürün Arayüzü ve Yönetişim

Hedef:
- Chat + dashboard deneyimi,
- Audit izi ve karar kayıtları,
- Kalite ve güvenlik kapıları.

Çıktı:
- Ürün sahibi odaklı yönetilebilir platform deneyimi.

## Faz 5 — Cloud Geçiş Hazırlığı

Hedef:
- Local-first prototipten cloud-native işletime geçiş planı,
- Sağlayıcı bağımsızlık prensibini korumak.

Çıktı:
- AWS/Azure ve benzeri sağlayıcılar arasında gerektiğinde geçiş yapılabilecek mimari temel.

---

## Teknik Ek

## As-is teknik dayanak

- Control Plane API: `agent-lab/control-plane/Program.cs`
- Worker runtime: `agent-lab/agent-worker/Program.cs`
- Local orchestration: `docker-compose.yml`
- Lifecycle scriptleri: `scripts/local.ps1`, `scripts/local.sh`
- Onboarding ve karar belgeleri: `docs/*`

## Mimari ilke vurguları

- İnsan onayı kapıları korunur.
- Güvenilmeyen ajan çıktısı, doğrulama olmadan kabul edilmez.
- Sağlayıcı bağımlılığı minimize edilir.
- Yerel prototip, cloud production kararının yerine geçmez.

---

## Sonuç

Bu proje, “tek komutla kod üreten bir ajan” hedefinden daha büyük bir iddia taşıyor: yazılım geliştirmeyi planlayan, yürüten ve denetlenebilir hale getiren bir platform.

Bir sonraki adımda ayrı bir belgede:
- local development işletim modeli,
- faz-2 cloud geçişi,
- barındırma ve aylık bakım maliyetleri,
- sağlayıcılar arası fiyat/performans yeniden değerlendirme kriterleri
birlikte ele alınacaktır.
