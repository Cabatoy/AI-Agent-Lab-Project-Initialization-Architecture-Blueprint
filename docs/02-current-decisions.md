# 02 - Mevcut Kararlar

Bu kayıt yalnızca brief'te açıkça kesinleşmiş kararları içerir. “Öneri” ve “Açık” başlıkları karar değildir; mimari blueprint'te ayrıca açıklanır.

## Kesinleşmiş

| Konu | Karar |
|---|---|
| Platform adı | AI Agent Lab |
| Ürün yönü | Birden fazla bağımsız yazılım projesini destekleyen genel amaçlı platform |
| İlk müşteri projesi | Pazaryeri; Agent Lab'dan bağımsız kalacak |
| Bulut sağlayıcısı | AWS seçildi; bu girişim için yeni/ayrı AWS hesabı planlanıyor |
| Bootstrap hesap e-postası (geçici) | Domain açılana kadar tüm ilk aboneliklerde kişisel Gmail kullanılacak: `altarailabsaassolutions@gmail.com` |
| Bölge tercihi | Frankfurt (`eu-central-1`) tercih edilen başlangıç bölgesi; kaynak oluşturma öncesi doğrulanmalı |
| Bütçe hedefi | Başlangıç altyapısı için yaklaşık 30-75 USD/ay hedef; mutlak üst sınır değil. Model/API tüketimi ayrıca hesaplanacak |
| Prototip öncesi erişilebilirlik ve maliyet | Kesintiler kabul edilebilir; prototip çıkana kadar öncelik minimum maliyet. Yüksek erişilebilirlik/HA bu aşamada zorunlu değil. Workflow verisi kalıcı olmalı ve kesinti sonrası devam edebilmelidir |
| Altyapı yaklaşımı | Başlangıçta EKS/Kubernetes'ten kaçın; yönetilen servisleri, konteynerleri, talep üzerine işlemeyi ve IaC'yi değerlendir |
| Bağımlılık sınırı | Kişisel bilgisayar, ev ağı veya ev sunucusu zorunlu çalışma bağımlılığı olamaz |
| Ajan organizasyonu | Ortak ajan havuzu ile proje özelindeki ajanları birlikte kullanan hibrit model |
| Ajan kavramları | Agent Definition ile Agent Instance birbirinden ayrı olmalı |
| İnsan denetimi | Üretim dağıtımı açık insan onayı gerektirir; ajanlar büyük mimari değişiklikleri tek başına kararlaştıramaz |
| Kullanıcı deneyimi | Dashboard ve Chat aynı Control Plane'i kullanmalı |
| Kaynak kontrolü | GitHub kullanılacak; uygulama repolarının private olması ve CI akışları hedefleniyor |
| Marketplace başlangıç biçimi | Önce responsive web; mobil uygulama daha sonra |
| Marketplace mimari yönü | Mikroservisler yerine başlangıçta modular monolith |
| Güvenlik yaklaşımı | Ajan üretimi güvenilmeyen kabul edilir; görevler izole edilmeli ve sırlar korunmalı |
| Local container runtime | Local prototip için Podman Desktop/Podman Compose kullanılacak; bu tercih cloud production runtime kararı değildir |
| Local lifecycle | `new-build` veriyi korur; `rebuild-all` ve `delete-all` volume verisini siler ve açık onay gerektirir |
| Çalışma aşaması | Mimari belgelendirme sürerken local Podman prototipi build edilip doğrulanıyor. Cloud provisioning henüz yapılmıyor |

## Öneriler (onay bekliyor)

- Kaynak kodu ve başlangıç belgelerini, bağımsız ve özel bir `ai-agent-lab` deposunda tutmak.
- Agent execution için ECS/Fargate'i ilk değerlendirme adayı yapmak.
- Workflow durumu ve uzun süreli onaylar için ajan framework'ünden ayrı bir durable workflow motoru değerlendirmek.
- IaC, CI kimlik doğrulaması ve sır yönetimi tasarımında AWS OIDC ve Secrets Manager'ı değerlendirmek.
- Başlangıçta az sayıda yönetilen bileşenle ilerleyip, SLO ve gerçek kullanım ölçülmeden yüksek erişilebilirlik mimarisine geçmemek.

## Henüz karar olmayan teknoloji adayları

ASP.NET Core/.NET, PostgreSQL, Entity Framework Core, SignalR, Next.js/TypeScript, Redis, S3, Terraform, ECS/Fargate, LangGraph, Semantic Kernel ve Temporal adaydır. Podman yalnızca local prototip için kesinleşmiştir; cloud container platformu kararı değildir. Podman AI Lab local model deneyi için opsiyoneldir ve Worker'a entegre edilmiş değildir.

## Karar kayıt kuralı

Yeni mimari karar; kapsamı, bağlamı, alternatifleri, sonuçları, onay tarihini ve sahibiyle birlikte [karar kayıtları](decisions/README.md) altında yazılacaktır. Bir öneri açıkça onaylanmadan bu tablonun “Kesinleşmiş” bölümüne taşınmamalıdır.