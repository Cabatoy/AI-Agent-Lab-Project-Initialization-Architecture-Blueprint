# HIC — Human in Control

**Kişiye uyarlanabilen, model bağımsız, insan denetimli AI agent platformu.**

HIC (AI Agent Lab), kullanıcıların dijital işlerini doğal dille tarif edip planlayabildiği, uygun yerel veya API tabanlı modellerden yardım alabildiği ve onaylı işleri izlenebilir bir süreçte yürütebildiği bir platform olarak geliştiriliyor. Hedef yalnızca kod üretmek değil: yazılım geliştirme, araştırma, içerik üretimi ve başka dijital iş akışlarını aynı denetim modeli altında desteklemek.

> **Durum: Aktif geliştirme / yerel prototip.** Bu depo production-ready bir hizmet, herkese açık çok kullanıcılı SaaS veya serbestçe kod çalıştırabilen bir ajan sunmuyor. Tasarlanan yeteneklerle mevcut uygulama özellikle ayrılmıştır.

## Ürün yaklaşımı

- **İnsan kontrolde:** Görev kapsamı, hassas entegrasyonlar, kod değişiklikleri, merge ve deployment gibi işlemler açık yetki ve doğrulanabilir kayıt gerektirir.
- **Model bağımsızlığı:** Yerel LLM'ler ve uyumlu bulut API'leri arasında seçim yapabilen bir altyapı hedeflenir. Model değişse de kullanıcının kalıcı bağlamı ve HIC'in kendi sistem bilgisi korunmalıdır.
- **Kullanıcıya özel deneyim:** Uzun vadede her kullanıcının kimlik doğrulamalı, verileri ayrılmış, özelleştirilebilir HIC çalışma alanı olacaktır. Kullanıcıya özel yetenekler daha sonra isteğe bağlı topluluk incelemesiyle ortak ürüne önerilebilecektir.
- **Güvenli uygulama:** Ajan görevleri için izole çalışma ortamları, en az yetki, kapsamlı testler, artefakt doğrulaması, audit ve geri alma gereklidir.
- **İnsan mesaj kuyruğu değildir:** Kullanıcının modeller, geliştirme araçları, CI ve GitHub arasında manuel kopyala-yapıştır yapması yerine onaylı iş akışlarını HIC koordine etmelidir.

İlk uygulama denemeleri yazılım geliştirme ve GitHub Draft PR döngüsüne odaklanıyor; bu, ürünün nihai kapsamının kod geliştirmeyle sınırlı olduğu anlamına gelmez. Başlangıçta düşünülen pazaryeri uygulaması ise platformun kendisi değil, olası bağımsız bir örnek projedir.

## Bugün ne var, ne henüz yok?

| Alan | Mevcut doğrulanabilir kapsam | Henüz hedef / kısıt |
| --- | --- | --- |
| Control Plane | .NET 10 API, görev/plan kayıtları, PostgreSQL ve Redis tabanlı yerel prototip | Genel kullanıma açık, tam yetkilendirilmiş çok kullanıcılı servis değil |
| HIC Web UI | React/Vite tabanlı yerel görev, plan ve durum ekranları; sınırlandırılmış operator akışları | Kullanıcı başına tam kişiselleştirme, production login ve hesap yaşam döngüsü tamamlanmadı |
| Model bağlantıları | Devre dışı bırakılabilen yerel model/provider iskeleti, Podman AI Lab, Ollama ve OpenAI-compatible yapılandırma seçenekleri | Model yönlendirme, kullanıcıya özel öneriler ve öğrenme sistemi hedef aşamasında |
| GitHub / Relay | Onay, Git planı ve Draft PR için geliştirilmekte olan, ayrı branch'lerde doğrulanan bileşenler | Tek HIC ekranından kesintisiz ve genel erişime açık koddan PR'a yolculuk henüz kabul edilmedi |
| İzolasyon | Native Host'un sınırlı salt okunur yetenekleri; sentetik ve offline sandbox güvenlik kontrolleri | Gerçek ajan kod yürütmesi için runtime izolasyonu henüz kanıtlanmadı; varsayılan olarak kapalı |
| Bulut | Sağlayıcıdan bağımsız mimari çalışmaları ve AWS/OCI için sınırlı preflight önerileri | Production deploy veya otomatik cloud provisioning olarak değerlendirilmemeli |

**Önemli:** Bazı yetenekler yalnızca açık **Draft PR**'larda bulunabilir; bu tabloda bir bileşenin anılması, onun `main` veya `develop` üzerinde birleştirilmiş olduğu anlamına gelmez. Mevcut branch'in gerçek kapsamını kod, test ve PR durumundan kontrol edin.

## Mimari — yüksek seviyede

```text
Kullanıcı → HIC Web UI
             ↓
       Control Plane (görevler, planlar, kararlar, audit)
             ↓
       Yetki ve güvenlik kapıları
             ↓
       Agent / model provider adaptörleri
             ↓
       İzole yürütme ortamı (hedef; kanıt olmadan kapalı)
             ↓
       Doğrulama, CI, GitHub Draft PR ve sonuç raporu
             ↳ HIC'e geri dönüş (uçtan uca entegrasyon hedefi)
```

Bugünkü yerel stack için PostgreSQL/Redis, .NET API/Worker, Native Host, Agent Bridge ve Web UI ayrı bileşenlerdir. Diyagram, planlanan akışın kavramsal özetidir; tüm okların bugün canlı çalıştığı iddiası değildir.

## Geliştirici için başlangıç

Bu depo öncelikle geliştirme ve güvenlik doğrulaması amacıyla kullanılır. Gereksinimler arasında **.NET 10 SDK**, Web UI için **Node.js/npm** ve yalnızca container tabanlı yerel senaryolar için **Podman/Compose** bulunur.

1. Depoyu klonlayın; kaynak kodu `HumanInControl.sln` ve `src/` altında inceleyin.
2. Güncel çalışma sınırlarını [devam/handoff notlarından](docs-md/continuation.md), IDE yönergelerini [.github/copilot-instructions.md](.github/copilot-instructions.md) dosyasından okuyun.
3. Salt okunur başlangıç rehberi için Windows'ta `./run.me.ps1`, macOS/Linux'ta `bash ./run.me` kullanın. Rehber, Podman kontrolü veya setup için ayrı onay adımları tanımlar.
4. Uygun ortamda `dotnet build HumanInControl.sln` ile derleyin. Web UI için `src/WebUI` altında `npm ci`, `npm test`, `npm run build` komutları bulunur.

**Container başlatma, mevcut veritabanına migration uygulama, worker execution, ağ ayarı değiştirme veya veri sıfırlama salt okunur adımlar değildir.** Bunlar için ilgili rehber ve açık onay kapılarını izleyin. Özellikle `-Reset`, `rebuild-all` ve `delete-all` veri silebilir. Sırları, token'ları veya şirket verilerini repoya, issue'lara ya da model istemlerine koymayın.

Ayrıntılı komutlar: [Local onboarding](docs-md/local-onboarding.md) · [Web UI geliştirme rehberi](src/WebUI/README.md).

## Kaynak kod ve belgeler

```text
src/ControlPlane/   .NET API ve görev/plan koordinasyonu
src/AgentWorker/    Worker ve model provider entegrasyonu
src/NativeHost/     Sınırlı native host / güvenlik sınırı
src/AgentBridge/    Geliştirici agent bridge bileşenleri
src/WebUI/          HIC Web UI (React / Vite)
tests/              .NET ve güvenlik testleri
deploy/             Yerel Compose ve izole test harness'leri
scripts/            Yerel launcher ve lifecycle scriptleri
docs-md/            Vizyon, kararlar, roadmap ve handoff
tools/              Geliştirici yardımcı araçları
```

- [Başlangıç proje vizyonu](docs-md/00-project-vision.md) ve [mimari ilkeler](docs-md/01-architecture-principles.md) — tarihsel başlangıç kararlarını içerir; yeni HIC vizyonunun tamamını yansıtmayabilir.
- [Mimari taslak](docs-md/04-architecture-blueprint.md) · [Uygulama yol haritası](docs-md/05-implementation-roadmap.md) · [Karar kayıtları](docs-md/decisions/README.md).
- [Güncel handoff](docs-md/continuation.md) · [Açık sorular](docs-md/03-open-questions.md).
- [GitHub Issues](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues) · [Pull Requests](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pulls).

## Geliştirme ve katkı ilkesi

Değişiklikleri varsayılan `main` branch'ine doğrudan yazmak yerine, bağımsız bir branch ve **Draft PR** ile sunun. `develop` entegrasyon hedefidir; stack'lenmiş PR'ların base/head ilişkileri ve exact-head CI sonuçları merge öncesinde doğrulanmalıdır. Güvenlik kapıları başarısızsa görev **BLOCKED** kalır; test atlanması başarı değildir.

HIC'in hedefi daha çok yetkiyi sessizce kullanmak değil, **kimin neyi, hangi kanıta dayanarak onayladığını görünür kılmaktır.**
