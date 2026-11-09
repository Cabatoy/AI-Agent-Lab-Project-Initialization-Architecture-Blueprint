# HIC eklenti araştırması ve geliştirme planı

**9 Kasım 2026 · Değerlendirilecek özellikler**

HIC için hedef, modeli değiştirdiğimizde bağlamı korumak, günlük model keşfini kullanıcının ortamına uyarlamak ve harici araçları kullanıcının izinleriyle mevcut görev akışına bağlamak.

| Sıra | Referans ve araştırma çıktısı | Ne zaman tekrar açılacak? | HIC kapsamı ve ilk kabul ölçütü |
| --- | --- | --- | --- |
| 1 | Claude-Mem, araç etkinliğini kısa gözlemlere dönüştürüp yeni oturuma bir indeks veriyor. Ayrıntılar ihtiyaç halinde getiriliyor. | Proje bağlamı, kalıcı hafıza veya model değiştirme iterasyonu başladığında. | Sürümlü HIC mimari/yetenek dokümanlarını, çalışma ortamı bilgisini ve proje kapsamındaki karar/devir kayıtlarını modelden bağımsız sakla. Yerel modelle başlatılan bir görevi başka model sürdürebilmeli. Amaç, karar, son test sonucu ve sıradaki adım kaynaklarıyla bulunabilmeli. İlk özetleme yerelde çalışmalı. |
| 2 | Agent Reach, web ve platform erişimini ortak bir arayüzde topluyor. Kaynak başına durum ve alternatif erişim yolları sağlıyor. Bazı sosyal kaynaklar oturum gerektiriyor. | Açılış ekranındaki günlük LLM önerileri iterasyonu başladığında. | Salt okunur kaynaklarla başla. Resmî doküman, model kartı ve GitHub release verilerini kaynak URL’si ve doğrulama tarihiyle kaydet. Aynı kaynak bağlantılarını ileride diğer araştırma görevlerine aç. |
| 2 | FreeLLMAPI, sağlayıcıları ortak API arkasına alıyor. Model ve anahtar bazında kota takibi, görev profilleri ve sağlayıcı geçişi sunuyor. Bulut istekleri etkin sağlayıcıya gidiyor. | Önce Model Radar kataloğu ve bağlantı testi, ardından model yönlendirme iterasyonu. | Yerel, ücretsiz API ve ücretli API ayrı görünsün. Öneri kartı donanım uyumu, görev türü, gerekli yetenekler, bağlantı ve bilinen kota durumunu göstersin. Bilinmeyen kota açıkça işaretlensin. Otomatik bulut geçişi veri paylaşım iznine, ücretli geçiş bütçe politikasına bağlı olsun. |
| 3 | Strix, kod ve çalışan web/API hedeflerinde aktif güvenlik testi yapıyor. Bulguları kanıt ve yeniden üretim adımlarıyla raporlayabiliyor. | Harici araç görevleri ve izole hedef yürütme hazır olduğunda. | İsteğe bağlı pilot. İzinli bir test uygulaması, tanımlı hedef kapsamı ve model bütçesiyle çalıştır. Bir bulgu yeniden üretilebilmeli. Kullanıcı düzeltme istediğinde mevcut HIC görev ve draft PR akışı kullanılmalı. |

## Günlük Model Radar ilk dilimi

1. Kullanıcının donanımı, görev tercihleri ve bağlı sağlayıcılarına göre yerel, ücretsiz API ve ücretli API adaylarını seç.
2. Kaynak verisini günlük yenile ve kartlarda son doğrulama tarihini göster.
3. Kartta hangi işe uygun olduğunu, kurulum veya bağlantı gereksinimlerini ve bilinen kısıtları açıkla.
4. İlk sürüm öneri ve bağlantı testiyle başlasın. Model yönlendirmesi, kota takibi ve veri paylaşım politikaları daha sonraki iterasyonda devreye girsin.

## Referanslar

- [Claude-Mem](https://github.com/thedotmack/claude-mem)
- [Agent Reach](https://github.com/Panniantong/Agent-Reach/blob/main/docs/README_en.md)
- [FreeLLMAPI](https://github.com/tashfeenahmed/freellmapi)
- [Strix](https://github.com/usestrix/strix)

İsim eşleştirmesinde FreeLLM API için `tashfeenahmed/freellmapi`, Strix için `usestrix/strix` esas alındı. Bulgular proje dokümanlarından geliyor. HIC üzerinde kurulum ve çalıştırma doğrulaması bekliyor.
