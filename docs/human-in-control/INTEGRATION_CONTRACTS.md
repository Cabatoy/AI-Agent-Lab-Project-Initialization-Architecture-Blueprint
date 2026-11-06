# HIC Integration Contracts

## Yönetici Özeti

HIC'in bileşenler arası iletişimi, sağlayıcıdan bağımsız ve sürümlü sözleşmelere dayanmalıdır. HIC plan/onay/policy sorumluluğunu üstlenirken, task lifecycle ve execution queue mevcut AI Agent Lab Control Plane/Worker'da kalır. İki ayrı task engine veya state machine oluşturulmaz.

Bugünkü API ve Worker, bu hedef sözleşmelerin tümünü uygulamıyor. Aşağıdaki JSON benzeri şekiller taslak gereksinimlerdir; mevcut API payload'larının birebir temsili veya onaylanmış wire contract değildir.

## Teknik Ek

### 1. Sözleşme kuralları

- Her mesaj/response `contractVersion` taşır; bilinmeyen major version güvenli biçimde reddedilir.
- `correlationId`, `planId`, `planVersion`, `taskId`, `runId` ve idempotency anahtarı varsa uçtan uca korunur.
- Zamanlar UTC; kalıcı kimlikler provider'dan bağımsızdır.
- Status değişikliği mevcut Control Plane state transition kurallarından geçer; yeni servis kendi paralel state machine'ini tanımlamaz.
- Retryable/permanent/cancelled/blocked ayrımı açık ve makinece işlenebilir olur; hata metni tek başına policy değildir.
- Sırlar, ham prompt, chain-of-thought ve gereksiz repository içeriği contract loglarına eklenmez.
- Retry ile yeniden teslim idempotent olmalı; aynı key ile yinelenen istek aynı operasyon sonucunu üretmeli veya açık conflict vermelidir.

### 2. HIC → Control Plane task dispatch

HIC dependency coordinator, hazır ve onaylı task'ı mevcut Control Plane üzerinden dispatch eder. Plan onayı belirli graph sürümüne ait olmalıdır.

Taslak request:

```json
{
  "contractVersion": "1",
  "correlationId": "uuid",
  "idempotencyKey": "stable-operation-key",
  "planId": "uuid",
  "planVersion": 1,
  "taskId": "uuid",
  "approval": {
    "approvalId": "uuid",
    "approvedBy": "principal-id",
    "approvedAtUtc": "timestamp"
  }
}
```

**Invariants**

- `taskId` execution plan ve `planVersion` ile ilişkili olmalıdır.
- Dispatch, plan sürümü onaylanmamışsa veya task'ın önkoşulları tamamlanmamışsa reddedilir.
- Onay bilgisi istemci beyanına körü körüne güvenerek yetki vermez; Control Plane kalıcı onay/policy kaydını doğrular.
- HIC yalnızca mevcut task queue/lifecycle yolunu çağırır.

### 3. Control Plane → Worker execution envelope

Taslak execution request:

```json
{
  "contractVersion": "1",
  "correlationId": "uuid",
  "idempotencyKey": "stable-run-key",
  "planId": "uuid",
  "planVersion": 1,
  "taskId": "uuid",
  "runId": "uuid",
  "task": {
    "title": "bounded task title",
    "description": "task-specific instruction",
    "inputReferences": ["authorized-artifact-reference"]
  },
  "executionPolicy": {
    "allowedCapabilities": ["capability-name"],
    "routePolicy": "local-first-no-implicit-cloud-fallback",
    "maxAttempts": 3,
    "timeoutSeconds": 300,
    "maxInputTokens": null,
    "maxOutputTokens": null
  },
  "cancellationToken": "opaque-cancellable-reference"
}
```

Bu örnekteki limitler şema örneğidir; `3`, `300` ve `null` onaylanmış ürün varsayılanı değildir. Gerçek contract'ta policy'nin kaynağı ve izin verilen sınırlar açıkça belirtilmelidir. Queue mesajı mümkün olduğunca küçük bir kimlik/zarf taşımalı; büyük prompt ve artefaktlar güvenli, yetkili referanslarla alınmalıdır.

### 4. Provider-neutral model request/result

Model adapter'ına giden request; provider özel route, HTTP header veya SDK tipini orchestration katmanına sızdırmamalıdır.

Taslak request alanları:

- Contract sürümü ve correlation/run/task kimlikleri.
- Capability/task sınıfı ve output format gereksinimi.
- Gerekli görev bağlamı veya yetkili input referansları.
- İzinli provider/model policy ve local/cloud sınıfı.
- Attempt, timeout, cancellation ve token/budget sınırı.

Taslak result:

```json
{
  "contractVersion": "1",
  "correlationId": "uuid",
  "taskId": "uuid",
  "runId": "uuid",
  "status": "succeeded",
  "output": {
    "content": "task result",
    "artifactReferences": []
  },
  "usage": {
    "inputUnits": 0,
    "outputUnits": 0,
    "unit": "tokens",
    "measurement": "actual"
  },
  "provider": {
    "adapter": "adapter-id",
    "model": "model-id",
    "route": "local"
  },
  "timing": {
    "startedAtUtc": "timestamp",
    "completedAtUtc": "timestamp"
  },
  "error": null
}
```

Hata halinde `status` açıkça `retryable_failure`, `permanent_failure` veya `cancelled` gibi sözleşmeli bir değere; `error` ise güvenli hata kodu ve redacted mesajına dönüşür. `usage.measurement`, gerçek sağlayıcı ölçümünü `estimated` ve `unknown` değerlerinden ayırır. Adapter kullanım verisi sağlayamıyorsa sıfır token uydurmaz; ölçüm `unknown` olur.

### 5. Approval/audit contract

Her insan kararı en az şu bilgileri saklamalıdır:

- Karar kimliği, principal/actor ID ve UTC zaman.
- Nesne türü ve ID; plan/task sürümü.
- `approve`, `reject`, `pause`, `resume`, `cancel` gibi eylem.
- Gerekçe veya kullanıcının sağladığı kısa not; hassas veri için redaction.
- Kararın hangi scope/permission ve policy kontrolüne dayandığı.
- Önceki ve sonraki durum; correlation ID.

UI/API istemcisi actor bilgisini yetkili kimlik doğrulamadan alır; istemcinin gönderdiği keyfi actor string yetkilendirme kaynağı değildir. Authentication/authorization altyapısı henüz doğrulanmış değildir ve ayrı tasarlanmalıdır.

### 6. Dependency ve scheduler contract

Plan graph payload'ı sürümlü bir plan sürümüne bağlı düğümler ve kenarlar taşır. Kenar `prerequisiteTaskId -> dependentTaskId` anlamına gelir. Kaydetme/onay sınırında aynı planın task'ları, self-edge, duplicate edge ve cycle denetlenir. Scheduler sonucu “dispatch edilebilir task ID'leri” ve bekleme/block nedenlerini üretir; kendi task state'ini saklamaz veya mevcut status kurallarını atlamaz.

### 7. Retry, recovery ve Redis/PostgreSQL tutarlılığı

- Retry mesajı aynı task/run/idempotency kimliklerini taşır; her deneme ayrı attempt olarak izlenir.
- PostgreSQL'deki task status ve transition history kalıcı audit gerçeğidir; Redis mesajı yürütme tetikleyicisidir, tek başına task'ın tek gerçek kaydı değildir.
- Dispatch intent ve task state'in transaction sınırı, outbox/publisher tekrar davranışı, Worker claim/lease ve recovery süresi uygulamadan önce kararlaştırılmalıdır.
- Eski attempt'in geç gelen status update'i daha yeni veya terminal durumu ezmemelidir.
- DLQ replay için yetki, gerekçe, attempt limiti ve audit gerekir; otomatik/sonsuz replay yoktur.

Bu garantiler mevcut prototipte tamamlanmış kabul edilmez. Worker'ın process içi in-flight dictionary'si kalıcı idempotency/lease yerine geçmez.

### 8. Güvenlik, sürümleme ve uyumluluk

- Egress, araç, repo ve artifact erişimi görev bazlı allowlist ile sınırlanmalıdır.
- Production sırları Worker/model prompt'una verilmemelidir.
- Artifact referansı yetkili, kapsamlı ve gerektiğinde kısa ömürlü olmalıdır; contract içine erişim token'ı gömülmez.
- Major değişiklik yeni contract version gerektirir; minor/additive değişiklik için uyumluluk ve tolerans politikası tanımlanır.
- CI'da schema validation, serialization/round-trip, unknown version, redaction, duplicate delivery ve provider adapter contract testleri bulunmalıdır.
- API/contract dokümantasyonu ve otomatik testler aynı sürüm sözleşmesinden türetilmeli veya drift kontrolü yapılmalıdır.

### 9. Henüz açık kalan entegrasyon kararları

- Execution envelope Redis mesajında mı taşınacak, yoksa queue yalnızca kalıcı dispatch kaydına referans mı içerecek?
- Outbox, claim/lease ve retry için en küçük yeterli kalıcı mekanizma nedir?
- Authentication principal, onay aktörü ve servisler arası yetkilendirme nasıl sağlanacak?
- Plan sürümü ve task graph hangi kalıcı kaynakta tutulacak; mevcut task şemasıyla ilişki nasıl kurulacak?
- Hangi provider capability'leri ve local model adapter'ları desteklenecek?
- Artifact store, saklama süresi, veri sınıfı ve cloud geçiş sınırı nedir?

Bu sorular çözülmeden taslak mesaj örnekleri uygulama garantisi olarak kullanılmamalıdır.
