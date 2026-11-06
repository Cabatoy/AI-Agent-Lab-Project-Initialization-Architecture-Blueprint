# Human In Control (HIC) Foundation

## Yönetici Özeti

HIC, mevcut AI Agent Lab'ın yerine geçen yeni bir yürütme motoru değildir. AI Agent Lab'ın Control Plane, Worker, task durum makinesi, PostgreSQL ve Redis bileşenlerini koruyarak bunların üzerine governance, interaction ve product layer eklemeyi hedefler.

**Motto:** İnsanlar mesaj kuyruğu değildir.

Bu belge seti Phase 0 temelini ve B0 paralel Agent Bridge tasarımını içerir. Öneriler mevcut koda eklenmiş özellikler veya onaylanmış ürün/sağlayıcı seçimleri değildir. HIC; açık insan onayını, denetlenebilirliği, güvenli devamı, provider bağımsızlığını ve gereksiz model bağlamı/tüketimini azaltmayı esas alır.

## Bugünkü durum

### Doğrulanmış mevcut işlevler

- `AI-Agent-Lab.sln`, Control Plane API, Agent Worker, RunMe aracı ve iki test projesini içerir.
- Beş proje .NET 10 hedefler. Control Plane Dapper, Npgsql ve Redis kullanır. PostgreSQL task kayıtlarını, task durum geçiş geçmişini ve execution plan kayıtlarını tutar.
- Task API'sinde task oluşturma/listeleme/detay, onay, retry ve durum güncelleme uçları bulunur. Durum geçişleri merkezi bir kuralla doğrulanır.
- Plan API'sinde `/plans`, `/plans/generate` ve `/plans/{id}/approve` bulunur. Plan oluşturma mevcut durumda talep metadata'sından deterministik bir plan özeti iskeleti üretir.
- Worker Redis kuyruğundan task alır, task ayrıntısını Control Plane'den yükler, opsiyonel model istemcisini çağırır, durumu günceller ve terminal hataları dead-letter kuyruğuna yazabilir.
- Worker'da sınırlı retry ve process içi tekrar işleme koruması vardır. Bu koruma kalıcı, çoklu Worker idempotency'si veya çökme sonrası güvenli queue recovery garantisi değildir.
- `podman-ai-lab`, `ollama` ve `openai-compatible` yapılandırma adları aynı OpenAI-uyumlu HTTP istemci uygulamasına yönlenir. Bu, provider-neutral routing katmanı değildir.
- R1 atomik task transition/audit ve Worker'ın doğrulanmış `Executing` kabul kapısı uygulanmıştır. Önceki checkpoint'te Release build ve 77/77 test (dört gerçek PostgreSQL integration testi dahil) geçmiştir; tarihsel baseline 23/23'tür.
- Önceki stub E2E R1 uygulama değişikliklerinden önce yapılmıştır; R1 container E2E henüz yapılmadı. Bu B0 dokümantasyon turunda testler veya canlı runtime tekrar doğrulanmadı; ayrıntılar [devam noktasındadır](../../docs-md/continuation.md).
- Project registry, durable execution ID/lease, bridge API, MCP server ve otomatik ChatGPT/Copilot handoff uygulanmış değildir. Tracked dosyalarda GitHub Actions workflow bulunmadı.

### Henüz uygulanmamış hedefler

Task dependency graph, topolojik scheduling, gerçek blocked-task çözümü, plan reddi/replanning, kalıcı execution recovery, provider-neutral routing, token/cost telemetry ve HIC dashboard mevcut değildir. İlk repository generation workflow'u da henüz yoktur.

## HIC'in sorumluluğu

HIC, kullanıcı talebini kontrollü bir işe dönüştürür; plan ve onayı görünür kılar; mevcut task engine'e uygun işi verir; ilerlemeyi, kararları, sonuçları ve harcamayı sunar. Var olan task state machine'in yerine ikinci bir state machine veya orchestration engine getirmez.

## Teknik Ek

- [Mimari ve sınırlar](./ARCHITECTURE.md)
- [Yol haritası ve kabul ölçütleri](./ROADMAP.md)
- [Token ve maliyet ekonomisi](./TOKEN_ECONOMY.md)
- [Entegrasyon sözleşmeleri](./INTEGRATION_CONTRACTS.md)
- [Agent Bridge B0 tasarımı ve entegrasyon değerlendirmesi](./AGENT_BRIDGE.md)
- Mevcut proje bağlamı: [README](../../README.md), [mimari ilkeler](../../docs-md/01-architecture-principles.md), [mevcut kararlar](../../docs-md/02-current-decisions.md), [açık sorular](../../docs-md/03-open-questions.md) ve [devam noktası](../../docs-md/continuation.md).

## Faz sınırı

Bu B0 teslimi yalnızca dokümantasyondur. Track A güvenilirlik ve Track B Agent Bridge planlaması paraleldir; tek orchestration otoritesi Control Plane'dir. Kaynak kodu, şema, container, altyapı veya Git branch/worktree değişikliği içermez. Her uygulama işi ayrıca onaylanmalıdır.
