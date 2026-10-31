# 03 - Açık Sorular

Durumlar: **Açık** henüz karara bağlanmadı; **Öneri** tartışma için başlangıç yönüdür. Hiçbir öneri onaylanmış karar değildir.

## 1. Prototip sonrası erişilebilirlik ve servis seviyesi ne olmalı?

- **Soru:** Prototip sonrasında Control Plane hangi erişilebilirlik, kurtarma süresi (RTO) ve veri kaybı hedefini (RPO) sağlamalı?
- **Neden önemli:** Kullanım ve üretim beklentileri büyüdükçe erişilebilirlik, veritabanı, ağ, yedekleme ve gözlemlenebilirlik maliyetlerini artırabilir.
- **Seçenekler:** Tek bölgeli düşük maliyetli devam; iş ihtiyacına göre kademeli dayanıklılık; daha yüksek erişilebilirlik için yedekli dağıtım.
- **Bağımlılıklar:** Prototip sonrası kullanıcı/iş yükü, kabul edilebilir kesinti, workflow kurtarma gereksinimi ve bütçe.
- **Öneri:** Prototip aşamasında onaylandığı üzere kesintileri kabul edip sabit maliyeti düşük tutmak; prototip sonuçları ve kullanım ölçümleriyle üretim öncesi SLO/RTO/RPO belirlemek.

## 2. Ajan orkestrasyonu ile durable workflow nasıl ayrılacak?

- **Soru:** LangGraph, Semantic Kernel, Temporal veya başka bir çözüm hangi sorumluluğu üstlenecek?
- **Neden önemli:** Akıl yürütme/araç kullanımı ile uzun süren işlerin kalıcı durumu, retry, timeout ve insan onayı farklı problemlerdir. Bunları tek çerçevede toplamak kilitlenme veya operasyon yükü doğurabilir.
- **Seçenekler:** Tek framework; ajan runtime'ı ile workflow motorunu ayırmak; ilk sürümde sınırlı bir uygulama içi durum makinesi.
- **Bağımlılıklar:** .NET/Python sınırı, workflow süreleri, bekleme/onay adımları, hata kurtarma gereksinimi.
- **Öneri:** Adayları ayrı sorumluluklar için değerlendirmek; ancak iki motorun operasyon ve entegrasyon maliyeti gerçek gereksinimle doğrulanırsa birleştirmek.

## 3. İlk sürümün Control Plane dağıtım biçimi ne olacak?

- **Soru:** API, web arayüzü, worker ve veritabanı prototipte hangi AWS servislerinde çalışacak; prototip sonrası hangi koşulda daha yüksek erişilebilirliğe geçilecek?
- **Neden önemli:** Basit işletim ile maliyet, ölçekleme ve dayanıklılık arasında doğrudan tercih oluşturur.
- **Seçenekler:** Yönetilen uygulama platformu; ECS/Fargate servisleri; daha düşük maliyetli tek görev/instance yaklaşımı; bazı bileşenleri talep üzerine çalıştırmak.
- **Bağımlılıklar:** Prototipte minimum maliyet tercihi; ağ çıkışı/NAT ihtiyacı, servislerin çalışma gereksinimi ve prototip sonrası SLO.
- **Öneri:** Prototipte kesinti toleransını kullanarak en düşük sabit maliyetli dağıtımı değerlendirmek; workflow verisini kalıcı tutmak; Kubernetes kullanmamak başlangıç yönüdür.

## 4. Ajan execution güvenlik sınırı ve izin modeli ne olacak?

- **Soru:** Her görev için hangi izolasyon düzeyi, ağ erişimi, CPU/bellek/zaman/token limiti ve kimlik bilgisi modeli kullanılacak?
- **Neden önemli:** Ajan tarafından üretilen veya çalıştırılan kod güvenilmeyen kabul edilir; başka projelere veya AWS kaynaklarına erişim engellenmelidir.
- **Seçenekler:** Görev başına ephemeral konteyner; daha güçlü izolasyon gerektiren işler için ayrı hesap/VM tabanlı sınır; dış ağa kapalı veya izin listeli ağ politikası.
- **Bağımlılıklar:** Fargate uygunluğu, build/test gereksinimleri, GitHub erişimi, model ve paket indirme ihtiyacı.
- **Öneri:** Görev başına kısa ömürlü ortam, en az ayrıcalıklı kısa ömürlü kimlik ve egress izin listesi için tehdit modeli hazırlamak.

## 5. Agent Lab ve müşteri projeleri nasıl ayrılacak?

- **Soru:** Tek GitHub organizasyonu ve AWS hesabı mı, yoksa kontrol düzlemi, yürütme ve müşteri projeleri için ayrı sınırlar mı kullanılacak?
- **Neden önemli:** İzolasyon ve blast radius; faturalama, IAM ve günlük operasyon karmaşıklığıyla dengelenir.
- **Seçenekler:** Tek hesapta sıkı proje sınırları; ortam başına hesap; platform ve müşteri projeleri için ayrı hesaplar.
- **Bağımlılıklar:** İlk kullanıcı sayısı, hassas veri, üretim ortamı, organizasyon sahipliği ve AWS bütçesi.
- **Öneri:** Geliştirme aşamasında dahi proje kimliklerini ve yetkileri ayrıştırmak; hesap sınırını tehdit modeli ve maliyet üzerinden seçmek.

## 6. Repository ayrımı nasıl yapılacak?

- **Soru:** Platform kodu/belgeleri, IaC ve pazaryeri için ayrı GitHub repoları gerekli mi?
- **Neden önemli:** Erişim sınırları, CI/CD, değişiklik hızı, paylaşılabilir belgeler ve operasyonel yönetim etkilenir.
- **Seçenekler:** Tek monorepo; platform + IaC + ürün için ayrı repolar; yalnızca farklı güven sınırlarını ayıran karma düzen.
- **Bağımlılıklar:** Katkıcılar, yetki modeli, release bağımsızlığı, gizlilik ve repository yönetimi.
- **Öneri:** Ayrı yaşam döngüsü veya farklı erişim gereksinimi kanıtlanana kadar erken bölünmeyi sınırlamak; pazaryerini platformdan bağımsız tutmak.

## 7. Model Gateway ve veri işleme politikası ne olacak?

- **Soru:** Hangi sağlayıcılar, hangi görev verileriyle kullanılabilir; sağlayıcı seçimi ve fallback nasıl yönetilir?
- **Neden önemli:** Maliyet, kalite, kullanılabilirlik, veri konumu ve sağlayıcı sözleşmeleri değişebilir.
- **Seçenekler:** Uygulama içi sağlayıcı adaptörleri; ayrı gateway; yönetilen üçüncü taraf gateway.
- **Bağımlılıklar:** Sağlayıcı hesapları, veri işleme şartları, token ölçümü, görev sınıflandırması.
- **Öneri:** Önce sağlayıcıdan bağımsız bir iç sözleşme ve veri sınıflandırması tanımlamak; ayrı gateway'i ancak gerçek yönlendirme/operasyon değeri varsa kurmak.

## 8. İlk teknoloji yığını ve dil sınırı nedir?

- **Soru:** Control Plane için .NET, frontend için Next.js ve ajan tarafında .NET/Python nasıl konumlanacak?
- **Neden önemli:** Ekip becerisi, ajan ekosistemi, bakım, dağıtım ve runtime çeşitliliği belirlenir.
- **Seçenekler:** Uçtan uca .NET; .NET Control Plane + Python ajan runtime; TypeScript ağırlıklı yaklaşım; karma yığın.
- **Bağımlılıklar:** Framework değerlendirmesi, insan geliştirici deneyimi, model/araç SDK'ları ve operasyon kapasitesi.
- **Öneri:** Sorumluluk başına tek teknoloji tercih etmek; birden fazla dilin yalnızca somut ekosistem avantajı sağladığı durumda kabul edilmesi.

## 9. Kimlik, kullanıcı rolleri ve bildirimler nasıl sağlanacak?

- **Soru:** Platform erişimi, MFA/SSO, rol modeli ve bildirim kanalı ilk sürümde ne olacak?
- **Neden önemli:** Onaylar, audit trail ve yüksek etkili işlemlerin kim tarafından yapıldığını kanıtlamak gerekir.
- **Seçenekler:** Yönetilen kimlik sağlayıcı; AWS ile bütünleşik yaklaşım; sınırlı kullanıcı için uygulama içi kimlik.
- **Bağımlılıklar:** Kullanıcı sayısı, GitHub kimliği, erişim modeli ve bildirim tercihleri.
- **Öneri:** MFA ve denetlenebilir insan kimliği sağlayan yönetilen çözüm adaylarını incelemek; kesin sağlayıcı seçmemek.

## 10. Pazaryerinin ürün ve yasal kapsamı nedir?

- **Soru:** Hedef ülkeler, kullanıcı yaş aralığı, satıcı/alıcı rolleri, ödeme modeli ve kişisel verilerin niteliği nedir?
- **Neden önemli:** Yaş güvencesi, moderasyon, tüketici/ödeme yükümlülükleri, veri saklama ve güvenlik tasarımı bunlara bağlıdır.
- **Seçenekler:** Ülke ve ürün kapsamı netleştikten sonra hukuk/uyum uzmanlığıyla farklı lansman sınırları belirlemek.
- **Bağımlılıklar:** Ürün gereksinimleri, iş modeli, ödeme sağlayıcıları, hedef pazar ve hukuk danışmanlığı.
- **Öneri:** Marketplace geliştirme görevlerinden önce hedef pazar ve veri akışını tanımlamak; yasal uygunluk varsayımı yapmamak.

## 11. Yedekleme, saklama ve maliyet kontrolü nasıl yapılacak?

- **Soru:** Workflow, kaynak kod bağlantıları, loglar ve artefaktlar ne kadar saklanacak; bütçe eşiğinde sistem nasıl davranacak?
- **Neden önemli:** Uzun saklama maliyet ve gizlilik riski; erken silme kurtarma/audit kabiliyetini etkiler.
- **Seçenekler:** Sınıfa göre saklama süresi; kısa ömürlü log ve görev bazlı artefakt politikası; arşiv katmanı.
- **Bağımlılıklar:** Denetim ihtiyaçları, hukuki gereklilik, AWS bütçe alarmı ve RPO/RTO.
- **Öneri:** Her veri sınıfı için sahip, amaç, saklama ve silme kuralı belirlemek; bütçe alarmını otomatik durdurma ile karıştırmamak.