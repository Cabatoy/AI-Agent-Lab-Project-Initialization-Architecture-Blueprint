# AI Agent Lab - Copilot Talimatları

## Proje bağlamı

AI Agent Lab, birden fazla bağımsız yazılım projesini yapay zekâ ajanlarıyla planlamayı ve geliştirmeyi hedefleyen bir platformdur. İlk müşteri projesi pazaryeridir ve platformdan bağımsız kalmalıdır. Bu depo şu anda planlama ve mimari belgelendirme aşamasındadır.

## Dil ve iletişim

- Konuşmaları, açıklamaları, önerileri, soruları ve proje belgelerini doğal Türkçe yaz.
- Teknik terimler, teknoloji adları ve kod tanımlayıcıları İngilizce kalabilir.
- Kesinleşmiş kararları, önerileri ve açık soruları açıkça ayır; öneriyi sessizce karar haline getirme.

## Planlama ve uygulama sınırı

- Kullanıcı açıkça istemedikçe uygulama kodu yazma, araç kurma, AWS kaynağı oluşturma/provision etme, ücretli servis başlatma, GitHub deposu açma, commit veya deploy yapma.
- Mimari konularda önce seçenekleri ve operasyon/maliyet etkilerini açıkla; karar gerektiren noktada kullanıcı onayı al.
- Kullanıcı bir dokümantasyon değişikliği istediğinde yalnızca ilgili belgeleri güncelle; uygulama kapsamına kendiliğinden geçme.

## Bağlayıcı ilkeler

- Cloud-first ve insandan/kişisel cihazdan bağımsız yürütme. Ev bilgisayarı veya ev ağı hiçbir zorunlu bağımlılık olamaz.
- Her ajan görevi izole ve sınırlandırılmış çalışır; üretilen kod güvenilmez kabul edilir.
- Model sağlayıcısından bağımsızlık hedeflenir; sağlayıcı ve runtime tercihlerini onaysız kesinleştirme.
- İnsan plan onayı, PR incelemesi ve üretim dağıtımında açık onay kapıları korunur. Üretim dağıtımı insan onayı olmadan yapılamaz.
- Sırları asla yanıta, loga, dokümana veya repoya yazma; örneklerde gerçek sır kullanma.
- Windows ve macOS katkısını ve taşınabilir göreli bağlantıları gözet.

## Kararların bakımı

- [Mevcut kararlar](../docs/02-current-decisions.md) yalnızca açıkça onaylanan kararları içerir.
- [Açık sorular](../docs/03-open-questions.md) karara bağlanmamış başlıkları tutar.
- Mimari bir karar değiştiğinde ilgili karar kaydını ve etkilenen blueprint, roadmap, maliyet veya ilke belgelerini aynı değişiklikte güncelle.
- AWS, GitHub, framework, fiyat veya mevzuata ilişkin zamana duyarlı iddiaları doğrulamadan kesin bilgi gibi sunma.