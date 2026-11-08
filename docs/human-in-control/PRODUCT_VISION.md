# HIC — Ürün Vizyonu

## Yönetici Özeti

**Humans are not message queues.**

Human In Control (HIC), self-hostable, provider-neutral ve insan yönetimli bir
**AI Software Factory** hedefidir. Bir insanın proje hedefini tanımlaması;
ajanların planlama, uygulama, test, inceleme ve teslim hazırlığını onaylı
sınırlar içinde gerçekleştirmesi amaçlanır. Mevcut AI Agent Lab Control Plane
tek task/execution otoritesi olarak korunur; ikinci orchestrator, state machine,
Redis queue veya execution scheduler kurulmaz.

Ürünün birincil arayüzü gelecekte **kendi yerel barındırılan HIC Web UI'miz**
olacaktır. ChatGPT ve Copilot Fusion geliştirme araçları ve isteğe bağlı
entegrasyon kanallarıdır; HIC'i işletmenin zorunlu arayüzleri değildir.
Bu belge ürün yönünü tanımlar; UI, routing veya otonom yazılım üretiminin
tamamlandığı anlamına gelmez ve bunları uygulama yetkisi vermez.

### Durum ayrımı

| Durum | Kapsam |
|---|---|
| Uygulanmış | Mevcut .NET 10 Control Plane/Worker, PostgreSQL/Redis prototipi, Worker OpenAI-compatible istemcisi; B1.1 yerel snapshot ve B1.2 onay digest'ine bağlı publisher |
| Doğrulanmış | B1.3 tekrarında 141/141 Bridge testi, 107/107 mevcut offline regresyon, sıfır warning/error Release build; sentetik yerel bare remote commit/push/bütünlük testleri |
| Tasarlanmış | Bridge'in transport-neutral sınırı, insan onay kapsamı, yerel/isteğe bağlı cloud sorumlulukları |
| Planlanmış | HIC Web UI, provider registry/routing, bütçe telemetrisi, genel agent yönetimi ve repository generation workflow |

Gerçek GitHub snapshot yayını ve ChatGPT okuması bu offline kanıtın parçası
değildir. Track A'nın önceki R2 runtime kanıtı ve recovery sınırları
[R2 kaydında](./R2_EXECUTION_OWNERSHIP.md) tutulur; bu tur yeniden çalıştırılmaz.

## Teknik Ek

### 1. Birincil ürün yüzeyi

HIC Web UI'nin planlanan kapsamı:

- Projeler/workspace'ler, hedefler ve sürümlü task planları.
- Agent oluşturma/yönetme; model/provider registry ve capability görünümü.
- Execution izleme, blocker/hata görünümü ve insan onayları.
- İzinli Git repository, branch ve PR akışları.
- Token, maliyet ve bütçe görünürlüğü.
- Audit, sonuç ve artifact tarama.

UI mevcut Control Plane sözleşmelerini tüketir; kendi task gerçeğini üretmez.
Agent önerisi insan onayı değildir. Planlanan akış:

```text
HIC UI: hedef -> plan -> insan kapsam onayı
  -> mevcut Control Plane -> izole, policy-bound agent işi
  -> build/test/review -> sonuç ve artifact -> insan teslim/merge onayı
```

Bağımlılık koordinasyonu mevcut engine'e delege edilir; yeni bağımsız
scheduler oluşturulmaz. İzolasyon, versioned approval ve recovery'nin eksik
kısımları tamamlanmadan otomatik execution vaat edilmez.

### 2. Modeller değiştirilebilir kaynaklardır

Core execution/result sözleşmeleri transport veya provider SDK payload'una
bağlanmaz. Gelecekte değerlendirilecek adapter'lar:

- Mevcut yerel Podman-hosted ve yetkilendirilmiş Docker-hosted LLM endpoint'leri.
- OpenAI-compatible yerel inference server'ları.
- Ayrı yetkilendirme ve bütçe ile isteğe bağlı OpenAI API modelleri.
- Yetkilendirilmiş diğer ticari LLM API'leri.
- Kullanım koşulları ve programatik izinleri doğrulanmış coding-agent araç/SDK'ları.

Repo için onaylı container runtime **Podman** olmaya devam eder. Docker-hosted
endpoint uyumluluğu olası adapter kapsamıdır, runtime değişikliği kararı değildir.
Gelecekte routing, mevcut Worker OpenAI-compatible istemcisini yeniden kullanır;
bugünkü provider alias'ları bağımsız, capability-verified adapter sayılmaz.

ChatGPT aboneliği OpenAI API kullanım hakkı olarak kabul edilmez. Copilot
aboneliğinden programatik SDK/otomasyon izni çıkarılmaz. Güncel erişim,
lisans, fiyat ve veri koşulları entegrasyon öncesi ayrıca doğrulanır.

### 3. Local-first ve token ekonomisi

Routing önceliği:

1. LLM gerektirmeyen deterministik işlem.
2. Göreve uygun, yetkilendirilmiş yerel model.
3. Yalnız açıkça izin verilmiş ücretli/cloud model.

Capability matching, veri sınıfı, kalite, kapasite, bütçe ve onay policy'si
birlikte değerlendirilir. Yerel hata veya kapasite eksikliği **sessiz ücretli
fallback** başlatmaz. Gereksiz repository bağlamı, transcript ve tekrar çağrılar
taşınmaz. Bilinmeyen token/maliyet sıfır değildir: `unknown` ve null değerler
korunur. Genel usage/budget telemetry ve routing henüz uygulanmamıştır;
[Token Economy](./TOKEN_ECONOMY.md) hedef sözleşmeyi açıklar.

### 4. İnsan governance sınırı

Sonuç doğuran eylemler için açık insan onayı korunur:

- Policy'nin gerektirdiği ücretli provider escalation.
- Onaylanmamış repository/hedefe push ve dış veri aktarımı.
- PR merge, destructive komut ve production deployment.
- Credential, erişim veya permission değişiklikleri.

Onay kimliği, işlem, scope/version/digest, hedef, zaman ve karar audit edilebilir
olmalıdır. B1.2 CLI flag/digest kapısı tam bir kimlik doğrulamalı approval
registry değildir. Üretim deployment her durumda açık insan onayı gerektirir.
Model cevabı, GitHub comment'i veya snapshot yürütme yetkisi sağlayamaz.

### 5. Agent Bridge'in yeri

GitHub snapshot'ları ilk asenkron handoff transport'udur. Bunlar authoritative
task database, tek gelecek iletişim kanalı, execution ownership yerine geçen
mekanizma, canlı ChatGPT bağlantısı veya nihai HIC UI değildir.

Core snapshot/result sözleşmeleri transport-neutral kalır. İleride yetkili MCP
veya başka API adapter'ları aynı veri minimizasyonu ve governance sınırını
koruyabilir. B1.1/B1.2 yalnız sınırlı result-sharing sağlar; tam execution
contract'ı veya otomatik delegation değildir.
[Agent Bridge](./AGENT_BRIDGE.md) tarihsel tasarımı ve uygulama bağlantılarını taşır.

### 6. Deployment yönü ve güven sınırları

İlk ortam yerel Windows geliştirme makinesi, .NET 10 Control Plane/Worker,
PostgreSQL/Redis, Podman-hosted inference endpoint'leri ve GitHub entegrasyonudur.
İsteğe bağlı hybrid/cloud hosting **yerel kullanımın önkoşulu değildir**.
Self-hostable ürün kişisel cihaz/ev ağına mimari olarak kilitlenmemelidir;
başka yetkili host'a taşınabilirlik ve bağımsız kalıcı çalışma korunur.

Önceki cloud-first/AWS hedefi tarihsel platform yönüdür; bu ürün yönü cloud
provisioning yapmaz veya önceki karar kayıtlarını sessizce değiştirmez. Somut
AWS/Azure/diğer sağlayıcı seçimi, operasyon, maliyet, veri konumu ve göç planı
ayrı mimari onay gerektirir.

Herhangi bir servis public ağa açılmadan önce authentication/authorization,
project isolation, TLS, secret yönetimi, audit, rate limit ve untrusted-agent
execution sınırları tasarlanıp doğrulanmalıdır. Bugünkü local prototipin
internet üzerinde güvenli olduğu varsayılmaz.

### 7. Öncelikler ve kabul sınırı

[Roadmap](./ROADMAP.md) P0–P10 sırası ve Track A / Track B bağımlılıkları
korunur. Ürün vizyonu Reliability roadmap'ini değiştirmez; R3/R4, provider
routing ve Web UI bu checkpoint'in uygulama kapsamı dışındadır.
Development branch push onayı, snapshot candidate digest'inin export onayı
değildir. Gerçek snapshot yayını exact candidate ve remote revision için
ayrı insan kararı gerektirir.


---

## 8. Expanded North Star — adaptive digital work (2026-11-08 proposal)

> **Product direction update; not yet delivered.** HIC is not limited to an AI Software Factory. It aims to become a **local-first, user-shaped AI platform able to learn to perform any authorized digital task**, with a web workspace as the initial user-facing product. Software engineering remains the **first vertical slice**, not the permanent limit. Existing implementation/verification tables above are historical checkpoints and do not imply that this expanded vision is already implemented.

The LLM is **replaceable**. HIC's identity, architecture, verified capability descriptions, project/team/user context, decisions, task/audit history and approvals remain durable HIC-owned data, not hidden provider conversation state. Switching models reconstructs concise, versioned, permission-scoped context with trusted provenance and on-demand references; it must neither reset knowledge nor broaden authority. See [Self-Knowledge & Model Bootstrap Design](./SELF_KNOWLEDGE_DESIGN.md).

Every installation chooses local or explicitly approved cloud models and its own connectors. HIC can propose missing integration/skill/agent capabilities and, after user-approved isolated development and testing, let users retain them privately or explicitly offer reviewed upstream PR contributions. Keep the core small and stable; extensions never silently acquire secrets, permissions or deployment rights. Personal, project and team memories stay isolated and user-reviewable.

**Immediate delivery order is unchanged:** [#42](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/42) and downstream [#52](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/52) first prove real HIC UI -> authorized repository -> model coding -> tests -> scoped commit/push -> draft PR on an isolated test repository. [#55](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/55) only captures architecture/roadmap for now. No new task state machine, runtime deployment, self-installation, extension marketplace or model training is authorized by these documents.
