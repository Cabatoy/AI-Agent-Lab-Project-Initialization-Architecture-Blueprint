# AI Agent Lab - Copilot Talimatları

## Proje bağlamı

AI Agent Lab, birden fazla bağımsız yazılım projesini yapay zekâ ajanlarıyla planlamayı ve geliştirmeyi hedefleyen bir platformdur. İlk müşteri projesi pazaryeridir ve platformdan bağımsız kalmalıdır. Repo mimari belgeler ve Podman ile çalıştırılan local prototip içerir. Kullanıcı önce local akışı doğrulayıp ardından AWS'e taşımayı planlıyor; cloud provisioning henüz yapılmadı.

Yeni checkout veya sohbette önce dosyaları salt okunur denetle, ardından [devam noktasını](../docs/continuation.md) oku ve repo ile karşılaştır. Bu dosya güncel handoff kaynağıdır; sohbet geçmişinin otomatik olarak korunduğunu varsayma. Önerilen sonraki adımları onaylanmış karar gibi ele alma.

## Dil ve iletişim

- Konuşmaları, açıklamaları, önerileri, soruları ve proje belgelerini doğal Türkçe yaz.
- Teknik terimler, teknoloji adları ve kod tanımlayıcıları İngilizce kalabilir.
- Kesinleşmiş kararları, önerileri ve açık soruları açıkça ayır; öneriyi sessizce karar haline getirme.

## Planlama ve uygulama sınırı

- Kullanıcı açıkça istemedikçe uygulama kodu yazma, araç kurma, AWS kaynağı oluşturma/provision etme, ücretli servis başlatma, GitHub deposu açma, commit veya deploy yapma. Local bootstrap scriptleri ve ilgili prototip düzenlemeleri kullanıcı tarafından onaylandı.
- Mimari konularda önce seçenekleri ve operasyon/maliyet etkilerini açıkla; karar gerektiren noktada kullanıcı onayı al.
- Kullanıcı bir dokümantasyon değişikliği istediğinde yalnızca ilgili belgeleri güncelle; uygulama kapsamına kendiliğinden geçme.
- Local bootstrap sıfırlama akışında, kullanıcı yalnızca genel sağlayıcı seçenekleri yerine mevcut çalışan Podman AI Lab model hizmetleri arasında seçim yapmak isteyebilir.

## Bağlayıcı ilkeler

- Cloud-first ve insandan/kişisel cihazdan bağımsız yürütme. Ev bilgisayarı veya ev ağı hiçbir zorunlu bağımlılık olamaz.
- Her ajan görevi izole ve sınırlandırılmış çalışır; üretilen kod güvenilmez kabul edilir.
- Model sağlayıcısından bağımsızlık hedeflenir; sağlayıcı ve runtime tercihlerini onaysız kesinleştirme.
- İnsan plan onayı, PR incelemesi ve üretim dağıtımında açık onay kapıları korunur. Üretim dağıtımı insan onayı olmadan yapılamaz.
- Sırları asla yanıta, loga, dokümana veya repoya yazma; örneklerde gerçek sır kullanma.
- Windows ve macOS katkısını ve taşınabilir göreli bağlantıları gözet.
- Local container runtime kararı Podman'dır; Docker yalnızca Podman Compose'un kullandığı uyumlu Compose sağlayıcısı olarak görünebilir.
- `scripts/local.ps1` ve `scripts/local.sh` local stack lifecycle komutlarının tek kaynağıdır. Komut anlamını README ve scriptlerle birlikte güncelle.
- `run.me` ve `run.me.ps1` dosya denetimi ve handoff adımlarında yalnızca Chat'e gönderilecek mesajı gösterir; bu adımlarda hiçbir komut çalıştırmaz. `approve-podman` onayından sonra salt okunur Podman durum kontrolünü (`podman --version`, `podman machine list`, `podman compose version`, `podman compose ps`) kullanıcının onayıyla kendisi çalıştırır. Kontrolde sorun tespit edilirse ve kullanıcı `approve-setup` ile açıkça onaylarsa `scripts/local.ps1`/`scripts/local.sh` üzerinden yalnızca `new-build` komutunu çalıştırır. Bunların dışında başlatma, durdurma, ayar değiştirme, rebuild veya silme için ayrı açık onay gerekir; bu launcher'lar `rebuild-all`/`delete-all` gibi veri silen lifecycle komutlarını kendileri çalıştırmaz.
- `rebuild-all` ve `delete-all` kalıcı local volume verisini siler. İşlemi çalıştırmadan önce veri etkisini belirt ve açık onay al; scriptin confirmation bayrağını atlama.
- Windows user-mode networking ayarı diğer WSL dağıtımlarının ağını etkileyebilir. `-AllowWslNetworkingChange` bayrağını yalnızca etkisini açıklayıp açık onay aldıktan sonra kullan.
- Local Podman machine, Podman AI Lab veya geliştirici cihazı cloud çalışma zamanı için zorunlu bağımlılığa dönüşmemelidir. AI Lab henüz Worker'a entegre değildir.

## Kararların bakımı

- [Mevcut kararlar](../docs/02-current-decisions.md) yalnızca açıkça onaylanan kararları içerir.
- [Açık sorular](../docs/03-open-questions.md) karara bağlanmamış başlıkları tutar.
- Mimari bir karar değiştiğinde ilgili karar kaydını ve etkilenen blueprint, roadmap, maliyet veya ilke belgelerini aynı değişiklikte güncelle.
- Local Podman prototipi, lifecycle komutları ve devam bağlamı için [README](../README.md), [kararlar](../docs/02-current-decisions.md) ve [ADR-001](../docs/decisions/adr-001-podman-local-prototype.md) kaydını güncel tut.
- Büyük bir milestone tamamlandığında [devam noktasını](../docs/continuation.md) güncelle: doğrulanmış durum, açık teknik işler ve önerilen bir sonraki kontrol noktası. Mimari kararları ve açık soruları kendi kayıtlarında tut.
- AWS, GitHub, framework, fiyat veya mevzuata ilişkin zamana duyarlı iddiaları doğrulamadan kesin bilgi gibi sunma.