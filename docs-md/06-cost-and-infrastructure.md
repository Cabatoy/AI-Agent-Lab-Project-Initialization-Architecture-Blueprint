# 06 - Maliyet ve Altyapı

## Bütçe çerçevesi

Prototip çıkana kadar **minimum maliyet** önceliklidir ve servis kesintileri kabul edilebilir; bu dönemde yüksek erişilebilirlik (HA) zorunlu değildir. Başlangıç altyapısı için yaklaşık **30-75 USD/ay** hedefi korunmaktadır; bu bir hedef, kesin fiyat veya mutlak tavan değildir. AI model/API kullanımı bu hedefin dışında ayrıca hesaplanacaktır. Güncel fiyat, trafik, saklama, bölge ve seçilen servisler doğrulanmadan tutar taahhüt edilmemelidir.

Kesinti kabulü, workflow durumunu kaybetme veya kurtarılamaz görevler anlamına gelmez: kalıcı durum korunmalı ve servis geri geldiğinde yürütme güvenli biçimde devam edebilmelidir. Prototip sonrası SLO ve kurtarma hedefleri ayrıca belirlenecek. Aylık tahmin, prototip için minimum sabit tabanı ve normal/yoğun kullanım senaryolarını ayrı göstermelidir.

## Sabit altyapı maliyetleri

Olası kalemler: sürekli çalışan API/web servisi, yönetilen PostgreSQL kapasitesi, load balancing, ağ/NAT, DNS ve sertifika, log/metric saklama, yedekleme, güvenlik/audit kayıtları ve olası secrets/config servisleri. Her kalem ilk günden zorunlu değildir; seçim SLO ve güvenlik gereksinimiyle gerekçelendirilmelidir.

Özellikle NAT Gateway, sürekli veritabanı kapasitesi, load balancer ve log saklama düşük kullanımda dahi sabit veya taban maliyet yaratabilir. Bir servis “managed” olduğu için ücretsiz veya operasyon maliyetsiz varsayılmamalıdır.

## Talep üzerine işlem maliyetleri

Agent görevleri için konteyner işlem süresi, CPU/bellek, geçici depolama, ağ çıkışı, build/test süreleri ve artefakt aktarımı kullanım miktarına bağlıdır. ECS/Fargate adaydır; gerçek maliyet görev süresi ve eşzamanlılığa göre doğrulanmalıdır. Retry döngüleri ve başarısız görevler de tüketim üretir.

## AI model ve API tüketimi

Model maliyeti platform altyapısından ayrı raporlanır. Ölçüm en az sağlayıcı/model, görev türü, girdi/çıktı tokenı veya sağlayıcının fiyatlandırma birimi, retry, latency ve kalite sonucunu ilişkilendirmelidir. Bütçe uyarısı, sert kullanım limiti ve görevi durdurma davranışı ayrı politikalardır; hangilerinin desteklendiği sağlayıcı bazında doğrulanmalıdır.

Model gateway'in kendisi de çalıştırma veya lisans maliyeti doğurabilir. Sağlayıcı çeşitliliğinin operasyonel değeri, entegrasyon maliyetiyle karşılaştırılmalıdır.

## Üçüncü taraf servis maliyetleri

GitHub plan/Actions dakika ve artefakt saklama; model sağlayıcı hesapları; bildirim, kimlik, güvenlik tarama, domain ve ileride ödeme sağlayıcıları ayrı değerlendirilir. Kullanım, lisans ve sözleşme koşulları güncel kaynaklardan doğrulanmalıdır. Marketplace maliyeti Agent Lab bütçesine karıştırılmamalıdır.

## Gelecekteki isteğe bağlı ev altyapısı

Raspberry Pi veya ev Linux sunucusu ileride düşük riskli, isteğe bağlı deneyler için değerlendirilebilir. Elektrik, internet, yedek, donanım ömrü ve uzaktan erişim riskleri vardır. Platformun Control Plane'i, workflow verisi, zorunlu runner'ı veya yedek kopyası bu cihaza bağımlı olamaz.

## Maliyet yönetimi ve doğrulama

- Servis seçimi öncesi Frankfurt fiyatlarını ve servis mevcudiyetini resmi AWS fiyatlandırmasıyla doğrula.
- Tahmin tablosunda miktar varsayımı, birim fiyat kaynağı/tarihi, sabit/değişken ayrımı ve belirsizliği belirt.
- AWS hesap bütçesi ve uyarı eşikleri planla; uyarıların tek başına harcamayı durdurduğunu varsayma.
- Proje, görev ve model çağrısı bazında maliyet etiketleme/atfını tasarla.
- Egress, log, retry, yedek ve veri transferi maliyetlerini dahil et.
- Pilot ölçümünden sonra 30-75 USD/ay hedefini yeniden değerlendir; gerekli operasyon kalemlerini gizlemek için güvenlik veya kurtarma gereksinimini düşürme.