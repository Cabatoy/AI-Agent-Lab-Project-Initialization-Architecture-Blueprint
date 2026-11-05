# 05 - Uygulama Yol Haritası

Bu yol haritası sıfırdan üretime giden planı anlatır; bir satın alma, kurulum veya provisioning talimatı değildir. Teknoloji, maliyet ve hesap ayarları doğrulanıp açıkça onaylanmadan uygulanmamalıdır. Maliyetler bu aşamada **doğrulanacak** olarak işaretlenmiştir; kesin fiyat veya hesap yapılandırması varsayılmaz.

## Bölüm 1 — The Workstation

- **Hikâye:** Mevcut Windows iş bilgisayarı, platformu yönetmek ve gerektiğinde geliştirme yapmak için değerlendirilir; hizmetlerin çalışması ona bağlı olmaz.
- **Amaç:** Donanım, işletim sistemi, disk, bellek, güvenlik ve yedekleme yeterliliğini belirlemek.
- **Önkoşullar:** Donanım özellikleri ve geliştirici iş akışlarının toplanması.
- **Tedarik:** Satın alma yok; gerekirse yalnızca ölçüm sonrası öneri hazırlanır.
- **Kurulum kılavuzu:** Cihaz envanteri, disk şifreleme, güncelleme, MFA ve yedekleme adımları; uygulama/onay sonrası yazılacak.
- **Mimari:** İstemci cihaz yönetim aracıdır; cloud Control Plane ve görev yürütmesinin bağımlılığı değildir.
- **AI Agent görevleri:** Gereksinimleri listelemek ve donanım envanterini yorumlamak; satın alma kararı vermemek.
- **İnsan eylemleri:** Teknik özellikleri paylaşmak ve önerilen harcamayı değerlendirmek.
- **İnsan onayı:** Her donanım satın alımı öncesi.
- **Kabul ölçütleri:** Yeterlilik ve risk raporu; satın alma gerekip gerekmediğine dair gerekçeli karar.
- **Maliyet tahmini:** Doğrulanacak; mevcut donanımın ek maliyeti varsayılmaz.
- **Riskler:** Eksik cihaz bilgisi veya kişisel cihazın sürekli servis gibi kullanılmaya başlanması.
- **Teslimatlar:** Workstation envanteri ve geliştirici güvenlik kontrol listesi.

## Bölüm 2 — The Digital Foundation

- **Hikâye:** AWS, GitHub ve model sağlayıcılarına ait dijital hesaplar güvenli ve sahipliği belirli biçimde hazırlanır.
- **Amaç:** Ayrı AWS hesabı, GitHub organizasyonu/repoları, kimlik, MFA, bütçe alarmı ve izin sınırlarını tasarlamak.
- **Önkoşullar:** Hesap sahipliği, ödeme sorumlusu, hedef bölgeler ve ilk kullanıcılar netleşmeli.
- **Tedarik:** AWS ve model/API kullanım ücretleri; hesap açma veya ücretli hizmet seçimi ayrıca onaylanır.
- **Kurulum kılavuzu:** Root/administrative erişim, MFA, break-glass, bütçe uyarısı, audit ve GitHub yetkileri; sağlayıcı belgeleri doğrulanarak hazırlanacak.
- **Mimari:** Ayrı AWS hesabı ve Frankfurt bölge tercihi; hesap/env ayrımı hâlâ tasarlanacak.
- **AI Agent görevleri:** Güvenlik ve hesap kurulum kontrol listesi üretmek; hesap açmamak veya kaynak oluşturmamak.
- **İnsan eylemleri:** Hukuki sahiplik, ödeme yöntemi, MFA ve organizasyon erişim kararlarını vermek.
- **İnsan onayı:** Her hesap, abonelik, ücretli servis ve yetki değişikliğinden önce.
- **Kabul ölçütleri:** Hesap sahipleri, MFA, least-privilege taslağı, bütçe uyarı yöntemi ve olay iletişim noktası belgeli.
- **Maliyet tahmini:** Doğrulanacak; hesap, model sağlayıcıları ve üçüncü taraf ücretleri ayrı incelenecek.
- **Riskler:** Hesap kaybı, aşırı yetki, bütçe sürprizi, yanlış bölge veya sağlayıcı veri koşulları.
- **Teslimatlar:** Dijital hesap ve güvenlik tasarımı; gizli değer içermeyen işletim prosedürü.

## Bölüm 3 — The Developer Environment

- **Hikâye:** Windows ve macOS kullanan insanlar aynı projeyi tekrarlanabilir biçimde inceleyip test edebilir.
- **Amaç:** Git, Podman Desktop/Compose, .NET SDK, Node.js, AWS CLI ve Terraform için desteklenen sürüm/kurulum sözleşmesini belirlemek.
- **Önkoşullar:** İlk teknoloji kararları ve CI sürümleri netleşmeli.
- **Tedarik:** Araçların lisans/işletim maliyeti kontrol edilecek; satın alma varsayılmaz.
- **Kurulum kılavuzu:** `run.me` önce dosya denetimi ve handoff'tan sohbeti sürdürmeyi, açık onaydan sonra salt okunur Podman durum kontrolünü yönlendirir. Stack lifecycle'ı repo scriptleri yönetir; başlangıç için ayrıca onay gerekir. Podman Desktop kurulumu, desteklenen sürümler ve OS başına onboarding ayrıca belgelenir.
- **Mimari:** Yerel geliştirme isteğe bağlıdır; CI ve cloud iş akışları temel doğrulama yolu olmalıdır.
- **AI Agent görevleri:** Sürüm uyumu ve onboarding belgelerini üretmek, doğrulanmış komutları test etmek.
- **İnsan eylemleri:** Windows/macOS akışlarını gözden geçirip pratikliğini onaylamak.
- **İnsan onayı:** Desteklenen araç/sürüm listesi ve kurulum politikası öncesi.
- **Kabul ölçütleri:** Temiz cihazdan onboarding; aynı lint/test/build sonuçları; sırların yerelde saklanmaması.
- **Maliyet tahmini:** Doğrulanacak; lisanslı araç gereksinimleri ayrıca listelenecek.
- **Riskler:** OS farkları, SDK drift'i, yerel ortamın CI'dan ayrışması.
- **Teslimatlar:** Platformlar arası geliştirici rehberi, local stack komutları ve sürüm matrisi.

## Bölüm 4 — The First Agent

- **Hikâye:** Sınırlı ve geri alınabilir bir görev, maliyeti ve çıktısı ölçülebilen izole ajanda denenir.
- **Amaç:** İlk agent runtime'ını, model adaptörünü, araç izinlerini ve görev sınırlarını doğrulamak.
- **Önkoşullar:** Güvenlik modeli, sağlayıcı veri koşulları, görev bütçesi ve kabul kriterleri onaylanmalı.
- **Tedarik:** Model API tüketimi; çalışma zamanı bulut maliyeti; her ikisi doğrulanacak.
- **Kurulum kılavuzu:** Onaylı test projesi, sır yönetimi, limitler ve teardown adımları belgelenecek.
- **Mimari:** Agent orchestration ile durable workflow sorumlulukları ayrı test edilecek.
- **AI Agent görevleri:** Yalnızca test reposunda, izinli araçlarla görev yapıp çıktı/test/maliyet raporu üretmek.
- **İnsan eylemleri:** Test kapsamını ve model veri politikası sınırını belirlemek; sonucu incelemek.
- **İnsan onayı:** Model sağlayıcısı, runtime ve harcama üst sınırından önce.
- **Kabul ölçütleri:** Tekrarlanabilir görev; sınır dışı erişim reddi; hata sonrası izlenebilir durum; maliyet kaydı.
- **Maliyet tahmini:** Doğrulanacak; model/API ayrı gösterilecek.
- **Riskler:** Prompt injection, gizli veri sızıntısı, beklenmeyen token/compute tüketimi.
- **Teslimatlar:** Runtime karşılaştırması, güvenlik testleri ve ilk ajan değerlendirme raporu.

## Bölüm 5 — Building the Agent Lab

- **Hikâye:** Deneme görevinden, projeleri ve onayları yöneten platformun ilk dikey dilimine geçilir.
- **Amaç:** Control, Execution ve Integration Plane'in asgari uçtan uca akışını kurmak.
- **Önkoşullar:** Stack, durable workflow, kimlik, izolasyon, SLO ve maliyet kararları onaylanmış olmalı.
- **Tedarik:** AWS servisleri, domain/hosting ve model kullanımı; hepsi doğrulanacak ve ayrı onaylanacak.
- **Kurulum kılavuzu:** IaC tabanlı ortam oluşturma, yedekleme ve geri yükleme adımları tasarlanacak.
- **Mimari:** Proje/görev yönetimi, kalıcı workflow, Dashboard ve Chat, izole execution, GitHub/model adaptörleri.
- **AI Agent görevleri:** Onaylı backlog görevlerini uygulamak; her iş PR ve CI ile doğrulanmak.
- **İnsan eylemleri:** Kapsam ve API sözleşmelerini onaylamak; kritik mimari kararlara sahip çıkmak.
- **İnsan onayı:** Her dikey dilim ve kalıcı veri/kimlik değişikliğinden önce.
- **Kabul ölçütleri:** Talep > plan onayı > izole görev > test > PR > audit/maliyet görünürlüğü uçtan uca çalışır.
- **Maliyet tahmini:** Doğrulanacak; sürekli çalışan ve talep üzerine kaynaklar ayrıştırılacak.
- **Riskler:** Erken aşırı kapsam, durum tutarsızlığı, yedek/restore eksikliği ve kontrol düzlemi maliyetinin hedefi aşması.
- **Teslimatlar:** Üretim dışı ilk platform dilimi ve işletim/runbook taslağı.

## Bölüm 6 — The Agent Workforce

- **Hikâye:** Tek bir ajan denemesinden, ortak rol tanımları ve proje özelindeki uzmanların denetlenebilir görev paylaşımına geçilir.
- **Amaç:** Agent Definition/Instance, izin, model politikası, değerlendirme ve görev tahsisini uygulamak.
- **Önkoşullar:** Ajan kayıt şeması, proje izolasyonu ve ilk görev performansı anlaşılmış olmalı.
- **Tedarik:** Ek model kullanımı ve gözlemleme maliyeti doğrulanacak.
- **Kurulum kılavuzu:** Ajan tanımı yayımlama, sürümleme, geri alma ve değerlendirme prosedürü hazırlanacak.
- **Mimari:** Ortak havuz rol tanımları sağlar; proje tanımları dar kapsamlı özelleştirme yapar; instance her göreve bağlıdır.
- **AI Agent görevleri:** Uzman rolüyle sınırlandırılmış iş yapmak; kendi yetkisini veya kabul ölçütünü değiştirememek.
- **İnsan eylemleri:** Rol talimatlarını, yetki kapsamını ve kalite eşiklerini onaylamak.
- **İnsan onayı:** Yeni ajan rolü, yeni araç yetkisi ve model politikası için.
- **Kabul ölçütleri:** Ajan tanımı/instance ayrımı; proje izolasyon testleri; ölçülebilir kalite ve maliyet raporu.
- **Maliyet tahmini:** Doğrulanacak; rol başına model kullanımı ölçülecek.
- **Riskler:** Yetki yayılması, ajanlar arası döngü, koordinasyon maliyeti ve düşük kalite.
- **Teslimatlar:** Paylaşılan ajan kataloğu, proje ajanı şablonu ve değerlendirme seti.

## Bölüm 7 — Quality and Governance

- **Hikâye:** Ajan çıktısı hızlı olsa da güvenilir kabul edilmeden önce standart kontrollerden geçer.
- **Amaç:** Test, güvenlik, audit, maliyet limiti, onay ve kurtarma yönetimini güçlendirmek.
- **Önkoşullar:** Uçtan uca platform akışı ve hata kayıtları bulunmalı.
- **Tedarik:** Tarama/izleme servisleri ve saklama maliyetleri doğrulanacak.
- **Kurulum kılavuzu:** Olay müdahalesi, secret rotation, restore, iptal ve retry runbook'ları hazırlanacak.
- **Mimari:** PR CI kontrolleri, politika kapıları, denetlenebilir onaylar, veri saklama ve bütçe uyarıları.
- **AI Agent görevleri:** Test ve güvenlik bulgularını raporlamak; riskli bulguları gizlememek veya kapıyı aşmamak.
- **İnsan eylemleri:** Risk kabulü, politika istisnası ve üretim onayını yürütmek.
- **İnsan onayı:** Güvenlik istisnası ve üretim dağıtımında zorunlu.
- **Kabul ölçütleri:** Geri yükleme testi; iptal/tekrar deneme; yetki kontrolleri; audit bütünlüğü; bütçe alarmı testleri.
- **Maliyet tahmini:** Doğrulanacak; log ve artefakt saklama maliyeti ayrıca gösterilecek.
- **Riskler:** Alarm gürültüsü, test kapsamı açığı, gereğinden uzun saklama, kurtarma prosedürünün denenmemesi.
- **Teslimatlar:** Güvenlik ve kalite kapıları, saklama politikası, incident ve disaster recovery runbook'ları.

## Bölüm 8 — First Real Project

- **Hikâye:** Pazaryeri, Agent Lab'a bağımsız proje olarak alınır; ilk görev küçük ve geri alınabilir seçilir.
- **Amaç:** Ürün kapsamı, modüler monolit sınırları ve proje onboarding sürecini gerçek kullanımda doğrulamak.
- **Önkoşullar:** Hedef pazar, kullanıcı türleri, yaş/gizlilik/ödeme/moderasyon gereksinimleri ve platform güvenliği netleşmeli.
- **Tedarik:** Repo, model, depolama ve ödeme/üçüncü taraf maliyetleri; seçime kadar doğrulanacak.
- **Kurulum kılavuzu:** Ayrı repo, proje yapılandırması, veri sınıflandırması ve CI onboarding'i hazırlanacak.
- **Mimari:** Responsive web; modular monolith; Identity, Profiles, Media, Messaging, Discovery, Billing/Entitlements, Moderation ve Administration olası modüllerdir.
- **AI Agent görevleri:** Onaylı gereksinim ve küçük PR'lar; hassas veri ve ödeme alanları için ekstra test/güvenlik incelemesi.
- **İnsan eylemleri:** Ürün sahibi gereksinimleri önceliklendirir; PR'ları ve kapsamı inceler.
- **İnsan onayı:** Veri modeli, ödeme, yaş/güvenlik politikası ve lansman kararlarında.
- **Kabul ölçütleri:** İzole repo/CI; doğrulanmış ilk küçük özellik; erişim, gizlilik, test ve moderation gereksinimleri belgeli.
- **Maliyet tahmini:** Doğrulanacak; ürünün operasyon ve sağlayıcı maliyeti platform maliyetinden ayrı tutulacak.
- **Riskler:** Yasal gereksinimlerin yanlış varsayılması, hesap/veri izolasyonu, erken mikroservisleşme veya kapsam şişmesi.
- **Teslimatlar:** Marketplace ürün gereksinimi, modül haritası ve ilk onaylı PR.

## Bölüm 9 — Production Ready

- **Hikâye:** Doğrulanmış platform ve ürün, operasyonel sorumlulukları ve geri dönüş planı olan bir üretim hizmetine dönüşür.
- **Amaç:** Üretim/test ayrımı, gözlemlenebilirlik, yedek, kurtarma, erişim ve dağıtım kapılarını tamamlamak.
- **Önkoşullar:** SLO/RTO/RPO, tehdit modeli, veri saklama, olay müdahalesi ve üretim sahipliği onaylanmış olmalı.
- **Tedarik:** Üretim servisleri, domain, izleme, yedek ve model tüketimi fiyatları güncel kaynaklardan doğrulanacak.
- **Kurulum kılavuzu:** IaC deployment/rollback, restore, rotasyon, incident ve erişim gözden geçirme prosedürleri hazırlanacak.
- **Mimari:** Ortamlar arası IAM/ağ/veri ayrımı; GitHub OIDC adayı; gözlemleme ve bağımsız yedekleme.
- **AI Agent görevleri:** Release kanıtlarını ve test raporlarını hazırlamak; üretim onayı veya yetki yükseltmesi yapmamak.
- **İnsan eylemleri:** Go/no-go, üretim onayı, olay yönetimi ve periyodik erişim/maliyet incelemesi.
- **İnsan onayı:** Her üretim dağıtımında açıkça zorunlu.
- **Kabul ölçütleri:** Restore/rollback tatbikatı; onaylı SLO; güvenlik kapıları; alarm ve runbook; maliyet görünürlüğü; insan kontrollü release.
- **Maliyet tahmini:** Doğrulanacak; başlangıç hedefiyle farklar ve ölçek senaryoları açıklanacak.
- **Riskler:** Ölçülmeyen gerçek trafik, tek bölge arızası, yedek kurtarılamaması, operasyon yükü ve maliyet artışı.
- **Teslimatlar:** Üretim readiness değerlendirmesi, runbook, onaylı release süreci ve devam eden operasyon planı.