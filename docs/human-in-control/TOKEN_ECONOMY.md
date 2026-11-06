# Token and Cost Economy

## Yönetici Özeti

HIC'in hedefi yalnızca token fiyatını düşürmek değil; güvenilir sonucu en az gereksiz bağlam, model çağrısı ve retry ile elde etmektir. Deterministik işlerde kod kullanılır, LLM yalnızca gerçekten gerektiğinde çağrılır. İzinli ve yeterli yerel model, uygun görevlerde önceliklidir; cloud provider'a sessiz fallback yapılmaz.

Token/cost telemetry henüz uygulanmamıştır. Model usage ölçümü, planlanan execution contract'ın parçası olmalı ve task/run/provider/model ile ilişkilendirilmelidir. Sağlayıcının gerçek token ölçümü yoksa değer tahmin veya bilinmiyor olarak işaretlenmelidir.

## Teknik Ek

### 1. Ölçüm kapsamı

Her model execution için mümkün olduğunda şu alanlar ilişkilendirilir:

- `project_id`, `plan_id` ve plan sürümü, `task_id`, `run_id`, correlation/idempotency key.
- Provider adapter kimliği, provider, model ve model/endpoint türü; hassas endpoint veya credential değil.
- Görev sınıfı, local/cloud route ve route seçiminin policy nedeni.
- Input/output token sayısı veya sağlayıcının fiyatlandırdığı birim; ölçüm `actual`, `estimated` ya da `unknown` olarak nitelenir.
- Retry sayısı ve her attempt'in ayrı usage/maliyeti; toplam task maliyetinde double-count önlenir.
- Başlama/bitiş zamanı, latency, sonuç durumu, hata sınıfı ve doğrulama/kalite sonucu.
- Fiyat hesaplanıyorsa para birimi, fiyat kaynağı ve fiyatlandırma sürümü/geçerlilik zamanı.

Model dışı deterministic işlemler de sayılabilir (ör. CPU süresi, test süresi); bunlar model token maliyetinden ayrı raporlanır. Altyapı maliyeti, model/API tüketiminden ayrı tutulur.

### 2. Token verimli yürütme

1. İstek sınıflandırma, graph doğrulama, scheduling, onay/policy kontrolü ve sonuç biçimi kontrolünü deterministik kodla yap; LLM çağırma.
2. Her task'a yalnızca ihtiyaç duyduğu repository dosyalarını, kararları ve artefaktları ekle. Tam repo veya önceki konuşma varsayılan bağlam olmasın.
3. Prompt ve contract'ları sürümle; tekrar kullanılabilir sabit yönergeleri görev verisinden ayır. Sadece ölçülebilir fayda varsa context caching veya özet kullan.
4. Ajan işini küçük, sınırları belli task'lara böl; aynı içeriği birden fazla ajana göndermeyi gerekçesiz çoğaltma.
5. Çıktı formatı, uzunluk beklentisi ve kabul ölçütünü önceden tanımla; yalnızca eksik/başarısız kısımları tekrar çalıştır.
6. Retry yalnızca geçici hata sınıfları ve belirlenmiş sınırlarla yapılır; her attempt'in maliyeti ayrıca görünür.
7. Test ve statik analiz gibi deterministik kalite kapılarını model çağrısından önce/sonra uygun yerde kullan; aynı doğrulamayı gereksiz tekrar etme.

### 3. Routing ilkeleri

- **Deterministic-first:** Model gerektirmeyen görev, model servisine gönderilmez.
- **Local-first:** Görev veri sınıfı, kalite ve kapasite gereksinimiyle uyumlu izinli yerel model varsa tercih edilir.
- **Provider-neutral:** Sözleşme model sağlayıcısının özel JSON şekline bağlanmaz; adapter usage bilgisini ortak biçime dönüştürür.
- **No silent cloud fallback:** Yerel model yokluğu, hata veya kapasite yetersizliği cloud kullanımına kendiliğinden izin vermez. Policy ve gerektiğinde insan onayı aranır.
- **Quality and latency:** En ucuz seçenek otomatik olarak yeterli sayılmaz; kalite, gecikme, gizlilik ve hata oranı da ölçülür.
- **Explicit limits:** Token/harcama üst sınırları, uyarı, duraklatma ve onay davranışı ayrı policy kararlarıdır; bu belgede varsayılan değer belirlenmemiştir.

### 4. Bütçe ve kontrol davranışı

Her execution policy aşağıdaki parametreleri belirleyebilmelidir; değerler proje/talep bazında onaylanmalıdır:

- Input/output token veya provider pricing unit üst sınırı.
- Maksimum attempt/retry ve toplam elapsed time.
- Model ve route allowlist'i.
- Eşik yaklaşımında uyarı, bütçe aşımında duraklatma veya insan onayı isteme davranışı.
- Ücretsiz/yerel route başarısız olduğunda dış sağlayıcıya geçiş için ayrıca izin.

Budget guard, model çağrısından önce mümkünse beklenen kullanımın kabulünü kontrol eder, çağrıdan sonra actual/estimated tüketimi kaydeder. Tahmin ile actual farklıysa geçmiş kayıt düzeltilmeden, fark ve ölçüm kaynağı açıkça gösterilir.

### 5. Güvenlik ve gizlilik

- Prompt, ham model yanıtı veya repository içeriği telemetry tablosuna varsayılan olarak yazılmaz.
- API key, credential, authorization header ve secret hiçbir zaman metriğe veya loga girmez.
- Chain-of-thought veya gizli reasoning istenmez/saklanmaz; yalnızca görev sonucu, ölçülebilir kullanım ve gerekli hata sınıfı tutulur.
- Task/provider/model korelasyonu erişim kontrollü olmalı; saklama süresi ve silme politikası belirlenmelidir.
- Yerel ve cloud ölçümleri ayrı gösterilir; cloud veri aktarımı açık route/policy ile denetlenir.

### 6. Başarı göstergeleri

Dashboard/raporlamada en azından task başına başarı oranı, token/birim tüketimi, model ve retry maliyeti, latency, test/kalite sonucu, local/cloud dağılımı, tahmin-actual farkı ve tekrar çağrı oranı izlenmelidir. Bu göstergeler maliyet düşürmeyi kalite veya insan kontrolünü zayıflatmadan değerlendirmeye yarar.

Telemetry şeması ve raporlaması planlanmıştır; mevcut Worker bu ölçüm setini sağlamaz.
