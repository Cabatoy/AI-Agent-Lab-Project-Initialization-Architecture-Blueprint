# 01 - Mimari İlkeler

Bu ilkeler bağlayıcı ürün ve mimari kısıtlarıdır. Bunları uygulayacak teknoloji henüz seçilmiş olmayabilir.

## Bulut öncelikli, sürekli erişilebilir ve insandan bağımsız

- Platformun çalışması kişisel bilgisayara, ev ağına veya kişisel cihaza bağlı olamaz.
- Kontrol ve workflow durumu bulutta kalıcı olmalı; ajan, worker veya dağıtım kesintisinden sonra güvenli biçimde devam edebilmelidir.
- Görev yürütme gerektiğinde başlatılabilir; her bileşenin sürekli çalışması şart değildir.
- Prototip çıkana kadar servis kesintileri kabul edilebilir ve öncelik minimum maliyettir; bu aşamada yüksek erişilebilirlik (HA) zorunlu değildir.
- Kesinti toleransı kalıcı workflow durumunu veya kurtarılabilirliği ortadan kaldırmaz: servis yeniden erişilebilir olduğunda işler güvenli biçimde devam edebilmelidir.
- Prototip sonrası Control Plane erişilebilirlik hedefi, SLO ve RTO/RPO ayrıca kararlaştırılmalıdır; “Always-on” ifadesi tek başına belirli bir uptime taahhüdü değildir.
- Ev sunucusu veya Raspberry Pi ileride yalnızca isteğe bağlı iş yükü olabilir.

## Proje ve yürütme izolasyonu

- Her görev izole bir çalışma alanında çalışır.
- Proje verisi, ajan çalışma alanı, erişim belirteçleri ve yapılandırma kapsamlandırılır.
- Ortak ajan tanımları, projeler arasında veri paylaşımı veya sınırsız erişim hakkı vermez.
- Ajan kodu ve çıktıları test ve güvenlik kontrollerinden geçene kadar güvenilmez kabul edilir.
- Üretim kimlik bilgileri ajanlara verilmez; en az ayrıcalık ve kısa ömürlü yetkilendirme tercih edilir.

## İnsan denetimi ve değişiklik yönetimi

- Gereksinim ve teknik plan onay akışları kalıcı biçimde kaydedilir.
- Ajanlar büyük mimari değişiklikleri tek başına karara bağlayamaz.
- Kod değişiklikleri PR, otomatik doğrulama ve insan incelemesi üzerinden ilerler.
- Üretim dağıtımı açık insan onayı gerektirir.
- İptal, duraklatma, yeniden deneme, kurtarma ve denetim izi desteklenmelidir.

## Model bağımsızlığı

- Platform tek bir model sağlayıcısına bağlanmamalıdır.
- Sağlayıcı/model seçimi, görevin niteliği, kalite, gecikme, veri işleme koşulları ve maliyet dikkate alınarak yapılmalıdır.
- Model Gateway veya sağlayıcı soyutlaması öneridir; API sözleşmesi ve yönlendirme politikası henüz kararlaştırılmamıştır.

## Güvenlik ve gizlilik tasarımı

- Kimlik, yetki, sır yönetimi, ağ sınırları, loglardaki hassas veri ve veri saklama politikası tasarımın parçasıdır.
- Sırlar repoya, istemlere, ajan loglarına veya artefaktlara yazılmaz.
- Ajan araçları izin listeleri ve görev/proje kapsamıyla sınırlandırılır.
- Yedekleme ve felaket kurtarma, ana çalışma ortamından bağımsız olarak değerlendirilir.
- Pazaryeri için kişisel veri, yaş güvencesi, moderasyon, ödeme ve yasal yükümlülükler ürün lansmanından önce doğrulanmalıdır.

## Taşınabilirlik ve işletilebilirlik

- Belgeler ve iş akışları belirli bir VS Code kurulumu, kişisel yol veya AI asistanına bağlı olmaz.
- Windows ve macOS kullanan katkıcılar için aynı Git, test ve yerel geliştirme sözleşmesi hedeflenir.
- Bulut bileşenleri mümkün olduğunca yönetilen servisler ve talep üzerine çalışan işlem gücüyle kurulur.
- Maliyet, güvenlik, gözlemlenebilirlik ve kurtarma ilk sürüm tasarımında görünür olmalıdır.