# Ücretsiz Bulut API ve Yerel Uzman Model Havuzu

## Yönetici Özeti

- **Durum:** Değerlendirilecek feature / discovery önerisi; uygulanmış özellik veya kesinleşmiş sağlayıcı kararı değildir.
- **Tarih:** 8 Kasım 2026.
- **İlişki:** [Roadmap P6–P8](../../ROADMAP.md), [Token ekonomisi](../../TOKEN_ECONOMY.md).
- **Amaç:** HIC görevlerini ücretsiz bulut API'leri ve belirli görevlerde yeterli yerel modeller arasında, kaliteyi ve insan kontrolünü koruyarak çalıştırmanın fizibilitesini ölçmek.
- **Bu değişikliğin kapsamı:** Yalnız dokümantasyon. API hesabı, gerçek model çağrısı, kurulum, runtime değişikliği veya ücretli kullanım başlatmaz.

Önerilen ilk deney: Groq ve erişilebilir bir NVIDIA Nemotron endpoint'ini, bir küçük yerel kod modeli ve bir yerel embedding modeliyle karşılaştırmak. Gemini ve OpenRouter ikinci karşılaştırma adaylarıdır. Bu sıra uygulama onayı değildir.

Başarı ölçüsü ücretsiz token sayısı değil, **kota ve süre başına doğrulanmış tamamlanan görev** olmalıdır. Tek bir coding task çok sayıda model çağrısı tüketebilir. Ücretsiz servisler kapasite garantili production bağımlılığı sayılmamalıdır. Yerel bilgisayar da cloud yürütmenin zorunlu bağımlılığına dönüşmemelidir.

## Teknik Ek

### 1. Kavramlar ve uzmanlaşma

Model, API sağlayıcısı ve inference server ayrı tanımlanır. Nemotron model ailesidir; NVIDIA/OpenRouter erişim sağlayabilir. Ollama veya llama.cpp yerelde API sunabilir. OpenAI-compatible HTTP biçimi; tool calling, structured output, streaming ve cancellation davranışlarının aynı olduğunu garanti etmez.

Uzmanlık önce görev tanımı, ilgili repo/dokümanları getiren RAG, sınırlı araçlar ve kalite ölçümüyle kurulur. Fine-tuning başlangıç gereksinimi değildir. Sistem promptunda uzman rolü yazmak tek başına uzmanlık kanıtı değildir.

### 2. Adaylar ve değişebilir koşullar

Aşağıdakiler 8 Kasım 2026 tarihli belge kontrolüdür; canlı API/hesap doğrulaması yapılmadı. Kota, bölge, veri işleme ve model erişimi deney öncesinde yeniden doğrulanır.

| Aday | Gözlem | Değerlendirilecek rol |
|---|---|---|
| NVIDIA Build / Nemotron | Eski `nemotron-3-nano-30b-a3b` ücretsiz endpoint'i Deprecated; `nemotron-3-nano-omni-30b-a3b-reasoning` ücretsiz endpoint'i Available görünüyor. Deneme hizmetidir; sabit kota varsayılmaz. | Muhakeme, planlama, tool calling; coding görevlerinde ölçüm |
| Groq | Ücretsiz plan mevcut; RPM/RPD/TPM/TPD sınırları model ve organizasyon bazlı. | Hızlı sınıflandırma, özetleme, desteklenen modellerde araç kullanımı |
| Gemini API | Seçili modellerde ücretsiz katman; bütün modeller için geçerli değil. | Doküman analizi ve genel görevler |
| OpenRouter | `:free` varyantlarında 20 RPM ve başlangıçta 50 RPD; toplam en az 10 USD kredi alımıyla 1.000 RPD. Yüksek günlük sınır tamamen ücretsiz başlangıç değildir. | Tek bağlantı üzerinden model karşılaştırmaları |
| Yerel Qwen2.5-Coder 7B/14B | Kod odaklı başlangıç adayları; güncel en iyi model oldukları iddia edilmez. | Küçük patch, açıklama, test taslağı |
| Yerel EmbeddingGemma veya ölçülecek alternatif | Küçük embedding modeli; cevap üreten coding modelinin yerine geçmez. | Repo/doküman indeksi ve retrieval |

NVIDIA trial koşulları girdilerin/çıktıların kaydedilebildiğini ve ürün/model geliştirmede kullanılabildiğini belirtir. Kaynak kodun buluta çıkmasına proje ve route bazında izin verilmelidir. Diğer sağlayıcıların veri koşulları ayrıca incelenir.

### 3. Yerel deney sınırı

Mevcut geliştirici cihazı: Ryzen 9 PRO 8945HS, 64 GB RAM, Radeon 780M. İlk deney bir küçük kod modeli ve embedding servisiyle sınırlanmalı; birden çok büyük modelin aynı anda açık tutulması hedeflenmemeli.

RAM'e sığma hız garantisi değildir. Quantization, context/KV cache, eşzamanlılık, cold start ve bellek tüketimi ölçülür. Ollama kolay kullanım adayı; llama.cpp'nin Vulkan backend'i AMD için araştırma adayıdır. GPU uyumluluğu ve hız henüz doğrulanmadı. Mevcut Podman/lifecycle yaklaşımı korunur; bu kayıt yeni server veya container başlatmaz.

### 4. Önerilen entegrasyon

Mevcut Worker/provider configuration, P6 sözleşmesi, P7 routing ve P8 telemetry genişletilir; ikinci task state machine veya ayrı orchestration motoru kurulmaz. Uygulamadan önce güncel kaynaklar ve açık provider işleri tekrar incelenir; belgelerdeki tarihsel baseline güncel kod kanıtı sayılmaz.

Önerilen endpoint kaydı: provider/model kimliği, endpoint referansı, credential secret referansı, local/cloud konumu, doğrulanmış capabilities, context/output sınırı, concurrency, quota kaynağı ve kontrol tarihi, veri politikası ve fiyat türü (local/free-tier/trial/paid). Sırlar veya ham promptlar bu kayda yazılmaz.

Deterministik işler model kullanmaz. Yeterli ve izinli yerel model önceliklidir; ücretsiz cloud dahil her dış route açık politikaya tabidir. Ücretli fallback varsayılan kapalıdır. Sağlayıcı değiştirmek veri iznini genişletmez.

429/kota dolması durumunda bounded backoff ve Retry-After dikkate alınır. Görev mevcut engine üzerinden görünür biçimde bekler veya yalnız önceden izinli ve yeterli capability'ye sahip alternatif seçilir. Kimlik/config hataları retry döngüsüne girmez. Belirsiz tool yan etkisi başka modelle körlemesine tekrarlanmaz; mevcut ownership/idempotency/onay sınırları korunur.

### 5. Discovery işleri

- [ ] D1 — Güncel provider kodu ve P6–P8 açık işlerinin fark analizi; yeniden kullanılacak yüzeyleri belirle.
- [ ] D2 — Adayların hesap/bölge erişimi, kota, veri koşulları, trial süresi ve tool/JSON desteğini kaydet.
- [ ] D3 — Sabit commit üzerinde 12 sentetik görev hazırla: 3 sınıflandırma/özet, 3 retrieval, 3 küçük kod düzeltmesi, 3 çok adımlı araç kullanımı.
- [ ] D4 — Aynı görev/context/araç bütçesiyle adayları karşılaştır; desteklenmeyen görevleri ayrı raporla. Canlı çağrılar ayrıca kapsamlandırılır.
- [ ] D5 — Mock provider ile 429, timeout, cancellation, hatalı JSON, eksik usage ve kapalı ücretli fallback davranışlarını doğrula.
- [ ] D6 — Sonuç raporuyla Go / No-go / sınırlı pilot kararı öner; ardından ayrı uygulama backlog'u çıkar.

### 6. Kabul ölçütleri ve ölçüm

| Alan | Kaydedilecek kanıt |
|---|---|
| Kalite | Görev başarı oranı; coding görevlerinde patch incelemesi ve ilgili build/test sonucu |
| Agent davranışı | Geçerli tool çağrısı, schema uyumu, yanlış başarı bildirimi, gereksiz çağrı/retry |
| Performans | Uçtan uca süre, ilk yanıt, cold/warm ayrımı, yerel RAM ve concurrency |
| Kota/maliyet | İstek ve token tüketimi; actual/estimated/unknown ayrımı; retry dahil toplam; ücretsiz kota başına tamamlanan görev |
| Kontrol | Onaysız cloud/paid çağrı yok; local-only görev dışarı çıkmıyor; secret/prompt telemetry'ye yazılmıyor |
| Dayanıklılık | Kota dolması ve model yokluğunda görünür neden; bounded retry/cancellation; çift yan etki yok |

Pilot kabulü için kontrol testlerinin tamamı geçmeli, her adayın desteklediği görevlerde sonuçlar yayımlanmalı ve rol başına tercih gerekçesi gösterilmelidir. Sayısal kalite/hız eşikleri deneyden önce belirlenir; bu belgede ölçüm yapılmış gibi sonuç verilmez.

### 7. Açık sorular ve kapsam dışı

- Hangi projeler cloud'a kod/context gönderebilir?
- Öncelik sıfır ödeme mi, hız mı, coding başarısı mı? Tek seferlik kredi alımı kabul edilecek mi?
- Yerel servis yokken görev beklesin mi, önceden yetkilendirilmiş cloud route'a geçsin mi?
- En küçük yeterli model/context ve rol başına kabul eşiği nedir?

İlk kapsam fine-tuning, çoklu büyük model kurulumu, otomatik hesap/kota dolaşımı, provider garantisi veya production rollout içermez. Ana coding lifecycle işinin önceliğini değiştirmez.

### 8. Birincil kaynaklar

- [Nemotron Nano endpoint durumu](https://build.nvidia.com/nvidia/nemotron-3-nano-30b-a3b)
- [Nemotron Nano Omni endpoint ve trial koşulları](https://build.nvidia.com/nvidia/nemotron-3-nano-omni-30b-a3b-reasoning/deploy?nim=self-hosted)
- [Groq rate limits](https://console.groq.com/docs/rate-limits)
- [Gemini API pricing / free tier](https://ai.google.dev/gemini-api/docs/pricing)
- [OpenRouter ücretsiz modeller](https://openrouter.ai/collections/free-models)
- [OpenRouter limits](https://openrouter.ai/docs/api/reference/limits)
- [Qwen2.5-Coder model kataloğu](https://ollama.com/library/qwen2.5-coder)
- [Embedding modelleri](https://ollama.com/library?type=embedding)
- [llama.cpp runtime](https://github.com/ggml-org/llama.cpp)
