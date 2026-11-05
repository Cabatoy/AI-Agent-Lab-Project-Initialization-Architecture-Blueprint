# Human In Control (HIC) Foundation

## Yönetici Özeti

HIC, mevcut AI Agent Lab'ın yerine geçen yeni bir yürütme motoru değildir. AI Agent Lab'ın Control Plane, Worker, task durum makinesi, PostgreSQL ve Redis bileşenlerini koruyarak bunların üzerine governance, interaction ve product layer eklemeyi hedefler.

**Motto:** İnsanlar mesaj kuyruğu değildir.

Bu faz yalnızca mimari temeli ve sonraki işleri tanımlar. Bu belgelerde anlatılan öneriler, mevcut koda eklenmiş özellikler veya onaylanmış teknoloji seçimleri değildir. HIC; açık insan onayını, denetlenebilirliği, güvenli devamı, provider bağımsızlığını ve gereksiz model bağlamı/tüketimini azaltmayı esas alır.

## Bugünkü durum

### Doğrulanmış mevcut işlevler

- `AI-Agent-Lab.sln`, Control Plane API, Agent Worker, RunMe aracı ve iki test projesini içerir.
- Control Plane .NET 8, Dapper, Npgsql ve Redis kullanır. PostgreSQL task kayıtlarını, task durum geçiş geçmişini ve execution plan kayıtlarını tutar.
- Task API'sinde task oluşturma/listeleme/detay, onay, retry ve durum güncelleme uçları bulunur. Durum geçişleri merkezi bir kuralla doğrulanır.
- Plan API'sinde `/plans`, `/plans/generate` ve `/plans/{id}/approve` bulunur. Plan oluşturma mevcut durumda talep metadata'sından deterministik bir plan özeti iskeleti üretir.
- Worker Redis kuyruğundan task alır, task ayrıntısını Control Plane'den yükler, opsiyonel model istemcisini çağırır, durumu günceller ve terminal hataları dead-letter kuyruğuna yazabilir.
- Worker'da sınırlı retry ve process içi tekrar işleme koruması vardır. Bu koruma kalıcı, çoklu Worker idempotency'si veya çökme sonrası güvenli queue recovery garantisi değildir.
- `podman-ai-lab`, `ollama` ve `openai-compatible` yapılandırma adları aynı OpenAI-uyumlu HTTP istemci uygulamasına yönlenir. Bu, provider-neutral routing katmanı değildir.
- İki test projesindeki mevcut 23 test ve solution build doğrulanmıştır. Canlı Podman durumu bu dokümantasyon fazında doğrulanmış sayılmaz.

### Henüz uygulanmamış hedefler

Task dependency graph, topolojik scheduling, gerçek blocked-task çözümü, plan reddi/replanning, kalıcı execution recovery, provider-neutral routing, token/cost telemetry ve HIC dashboard mevcut değildir. İlk repository generation workflow'u da henüz yoktur.

## HIC'in sorumluluğu

HIC, kullanıcı talebini kontrollü bir işe dönüştürür; plan ve onayı görünür kılar; mevcut task engine'e uygun işi verir; ilerlemeyi, kararları, sonuçları ve harcamayı sunar. Var olan task state machine'in yerine ikinci bir state machine veya orchestration engine getirmez.

## Teknik Ek

- [Mimari ve sınırlar](./ARCHITECTURE.md)
- [Yol haritası ve kabul ölçütleri](./ROADMAP.md)
- [Token ve maliyet ekonomisi](./TOKEN_ECONOMY.md)
- [Entegrasyon sözleşmeleri](./INTEGRATION_CONTRACTS.md)
- Mevcut proje bağlamı: [README](../../README.md), [mimari ilkeler](../../docs-md/01-architecture-principles.md), [mevcut kararlar](../../docs-md/02-current-decisions.md), [açık sorular](../../docs-md/03-open-questions.md) ve [devam noktası](../../docs-md/continuation.md).

## Faz sınırı

Bu teslim Phase 0 dokümantasyonudur. Kaynak kodu, veritabanı şeması, container yapılandırması, cloud altyapısı veya branch üzerinde başka değişiklik içermez. Phase 1'e geçiş ve her uygulama işi ayrıca onaylanmalıdır.
