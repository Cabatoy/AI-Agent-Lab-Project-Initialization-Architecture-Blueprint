# P1A.2 — salt okunur authority sınırı ve anahtar işletimi

## Yönetici Özeti

**Canlı kullanım: BLOCKED.** Bu paket geliştiriciye özel, offline bir doğrulama
kütüphanesidir. Varsayılan bridge sağlayıcısızdır ve `blocked` döndürür.
Eklenen kod HTTP isteği, credential okuma, dosya yazma, task geçişi, claim,
lease, model/SDK/CLI oturumu veya Worker çağrısı yapmaz. Çalışan Windows
relay checkout'u, scheduled task ve `AckOnce`/`AckWatch` girişleri değiştirilmez.

`accepted-for-review`, yalnız bağımsız reviewer ve authority imzalarının
çapraz kontrolüdür; yürütme veya gerçek insan kimliği kanıtı değildir.
Her sonuçta `executionAllowed:false` korunur. Başarılı offline sonuçta bile
`ledgerVerified:false` ve `liveAuthorityVerified:false` kalır.

### İncelenen mevcut sözleşmeler ve BLOCKED kararı

| Kaynak | Doğrulanan durum / eksik |
|---|---|
| [Control Plane task API](../../../src/ControlPlane/Program.cs) | `GET /tasks/{id}` task içeriği ve zamanlarını okur; route üzerinde aktör doğrulaması, immutable revision veya imzalı authority yoktur. `POST /tasks/{id}/approve` legacy Worker kuyruğuna yazar; bu bridge tarafından **asla kullanılmaz**. |
| [TaskTransitionStore](../../../src/ControlPlane/TaskTransitionStore.cs) | Mevcut task state ve audit aynı transaction'da korunur; audit kaynağı insan kimliği/onay imzası değildir. İkinci task engine kurulmaz. |
| [LocalHealthRunner](../../../src/ControlPlane/LocalHealthRunner.cs) | Ayrı bootstrap token/session ile `system.health` yetkilendirmesi vardır. Bu, developer task revision/repository/ref veya Ed25519 snapshot authority'si değildir; token'ı yeniden kullanmayız. |
| [ExecutionSchema](../../../src/ControlPlane/ExecutionSchema.cs), [v3 migration](../../../src/ControlPlane/Migrations/003-native-run-approval-ledger.sql) | v2 startup sınırı korunur. Manuel v3 revision/approval/audit şeması gerçek ortamda uygulanmış varsayılmaz; repo/source bağları ve authenticated developer snapshot route'u yoktur. Migration çalıştırılmaz. |
| [P-256 issuer](../../../src/ControlPlane/NativeApprovalReceiptV2Issuer.cs), [receipt v2](../../../docs/human-in-control/SIGNED_APPROVAL_RECEIPT_V2.md) | Unwired native protokol `workspace.inspect` içindir. Ed25519 developer protokolüne otomatik dönüştürülmez veya authority yerine geçmez. |
| [Architecture](../../../docs/human-in-control/ARCHITECTURE.md), [integration contracts](../../../docs/human-in-control/INTEGRATION_CONTRACTS.md), [auth ADR](../../../docs/human-in-control/LOCAL_OPERATOR_AUTH_ADR.md), [v3 sınırı](../../../docs/human-in-control/NATIVE_APPROVAL_LEDGER_V3.md) | PostgreSQL tek task gerçeğidir. Loopback, GitHub yazarı, proxy token, UI boolean veya imza tek başına authenticated human approval değildir. |

Bu nedenle gerçek endpoint/persistence/auth implementasyonu bu PR'a eklenmez.
Aşağıdaki şema **bounded proposal** ve offline sözleşmedir; deploy edilmiş API
değildir. Gelecek authenticated Control Plane, mevcut task/revision/onay/audit
kaydından tutarlı tek read transaction ile üretmelidir. Snapshot kalıcı ikinci
task tablosu veya state machine değildir. Gerçek actor, revision, repository/ref,
iptal ve revocation epoch için güvenilir kayıt yoksa server snapshot üretmemelidir.
Read-only snapshot istemek, eksik onayı yaratamaz; approval mutation yasaktır.

## Teknik Ek

### 1. Sürümlü ve imzalı snapshot

[authority-snapshot.mjs](src/authority-snapshot.mjs) yalnız aşağıdaki tam
şekli kabul eder; bilinmeyen/eksik alanlar, major sürüm karışımı ve duplicate JSON
anahtarları reddedilir. Zarf ve payload sorted-key canonical UTF-8 JSON'dur;
base64url unpadded/canonical, Ed25519 imzası tam 64 byte'tır.
Zarf en çok 8192, decode edilen payload en çok 4096 byte olabilir.

Zarf alanları:

```text
version = hic.dev-authority-envelope/1
kind = SignedReadOnlyAuthoritySnapshot
algorithm = Ed25519
keyId, payload, signature
```

İmzalanan byte dizisi:
`HIC-DEV-AUTHORITY-SNAPSHOT-V1\n<keyId>\n<canonical payload bytes>`.
Reviewer zarfının farklı domain'i korunur; roller/anahtarlar birbirinin yerine geçmez.

Payload alanları:

| Alan | Zorunlu bağ / doğrulama |
|---|---|
| `version`, `kind` | `hic.dev-authority-snapshot/1`, `ReadOnlyTaskAuthority` |
| `issuer`, `audience` | `hic-control-plane-dev-authority`, `hic-dev-relay-review-only` |
| `serverOrigin` | Operatörün bağımsız verdiği tam HTTPS origin; path, userinfo, redirect veya transport'tan origin seçimi yoktur. Canlı default origin yoktur. |
| `repository` | Tam HIC repository kimliği |
| `taskId`, `taskRevision`, `taskStatus` | İmzalı review task ID'si, bağımsız review sırasında pinlenen pozitif revision, yalnız `Draft` |
| `actorId` | Bu v1'de signer reviewer kimliğiyle birebir aynı; delegation yoktur. Server bunu authenticated actor'dan almalıdır. |
| `orderId`, `reviewId`, `reviewerKeyId` | İmzalı reviewer zarfıyla birebir aynı |
| `reviewPayloadDigest` | Reviewer payload'unun canonical byte'larının SHA-256'sı; objective, branch, base SHA, path ve limitleri de bağlar |
| `source` | İmzalı reviewer `source` nesnesinin tamamı: repo, `hic-dev-orders`, queue issue, source issue ve bağımsız sağlanan exact reviewed Git SHA |
| `approval` | Aşağıdaki tam karar nesnesi |
| `sequence`, `revocationEpoch` | Pozitif safe integer; aynı task için sequence kesin artar, epoch/revision geri gidemez |
| `issuedAt`, `expiresAt`, `nonce` | Tam UTC millisecond ISO; istekten önce üretilmiş cache kabul edilmez, gelecekte issuedAt yoktur; ömür en çok 60 saniye, reviewer/issuer key ve review expiry'sini aşamaz. Nonce her istek için yeni UUID challenge'dır. |
| `executionAllowed` | Yalnız `false` |

`approval` tam alanları:
`approvalId`, `state=pending|approved|revoked`, `actorId`, `taskRevision`,
`intentDigest`, `decidedAt`, `expiresAt`, `revokedAt`.
`intentDigest`, reviewer `review.revisionDigest` ile aynı olmalı ve
`SHA256(canonicalJson(reviewedPayload.task))` olmalıdır. Önceki P1A'nın bu
digest'i keyfi bir değer olarak bırakan fixture'ları P1A.2 authority sayılmaz.
Pending kararda decision/revoke zamanı null'dır; approved kararda revoke null,
karar review'dan sonra ve snapshot'tan önce, grant ömrü en çok beş dakikadır.
Revoked kararda `decidedAt <= revokedAt <= issuedAt` gerekir.

### 2. Salt okunur adapter ve freshness

[ReadOnlyAuthorityBridge](src/authority-review.mjs) açıkça enjekte edilen
`authorityProvider.readSnapshot(request, {signal})` ve
`readTrustRegistry(signal)` dışında hiçbir sağlayıcı kullanmaz. İkisi de testte
fake'tir; canlı HTTP adapter, login, yeni CLI veya watcher dispatch yoktur.
`expectedGitSha` ve `expectedTaskRevision` GitHub metninden türetilmez; bağımsız
review context'inden gelmek zorundadır. Bu kütüphane GitHub CI'ı kendisi doğrulamaz.

- Reviewer imzası/key ömrü kontrol edilmeden authority read yapılmaz.
- İstek yalnız sürüm, audience, task/revision, order/review, payload digest,
  timestamp ve yeni nonce içerir; objective veya credential içermez.
- Beş saniyelik toplam deadline, abort ve tek in-flight inceleme sınırı vardır.
  Geç gelen cevap state'i değiştiremez; outage eski accepted sonucuna düşmez.
- Provider await sonrasında registry tekrar okunur; revoked/expired reviewer
  veya issuer, reviewer pin değişimi, stale cache ve yanlış nonce bloklanır.
- Aynı revision'da task intent veya source değişikliği epoch artsa bile
  equivocation'dır. Aynı revision/epoch'ta farklı approval da equivocation'dır.
  Approval değişince server epoch'u artırmalıdır.
- Sequence/epoch rollback ve key ID rebinding session'ı kilitler. Revoked
  approval ID daha yüksek epoch'ta yeniden canlandırılamaz.
- Path traversal, NTFS stream/drive biçimleri, reserved device names, trailing
  dot ve case-colliding path listeleri kabul edilmez. Dosya erişimi yapılmaz.
- Output yalnız sabit status/reason kodlarıdır; envelope, task text, yollar,
  exception mesajı veya key malzemesi loglanmaz.

Status'lar yalnız `blocked`, `awaiting-approval`, `accepted-for-review` olur.
Bridge'in bounded (1024 istek) process-içi challenge/high-water/pin gözlemleri
**ledger, claim, lease veya kalıcı replay kaydı değildir**. Kapasite dolunca
fail-closed durur; eski gözlemleri eviction ile unutmaz. Restart'ta yeni nonce
eski cevabın birebir replay'ini reddeder, fakat kalıcı rollback/fork tespiti için
transactional server epoch/sequence ve güvenilir recovery hâlâ gereklidir.
Yeni instance açmak canlı recovery izni değildir. Cached registry, tahmin edilen
epoch veya GitHub'dan gelen public key ile bu boşluk kapatılamaz.

### 3. Reviewer ve issuer key runbook — henüz uygulanmaz

1. **Bağımsız bootstrap:** Operatör, kimlik doğrulanmış yerel ceremony sırasında
   reviewer kimliği, yetki ve public SPKI fingerprint'ini ayrı kanaldan doğrular.
   Bu PR gerçek private key üretmez/saklamaz, credential kurmaz; CI yalnız
   açıkça sentetik deterministik Ed25519 fixture'ları kullanır.
2. **Pin:** Fingerprint `SHA256(DER SPKI)` lowercase 64 hex'tir. Registry entry
   yalnız `publicKeyPem`, `fingerprint`, `role=reviewer|issuer`, `identity`,
   `authorized`, `revoked`, `notBefore`, `expiresAt` alanlarını taşır.
   Sadece public-key PEM ve Ed25519 kabul edilir. Issuer/reviewer farklı roller,
   farklı key ID ve anahtarlardır; GitHub/order/PR bunları enroll edemez.
3. **Storage sınırı önerisi:** Registry ve revocation high-water, git checkout,
   senkronize klasör, relay inbox ve makbuz dizininin dışında operator/authority
   kontrollü, ACL ile kısıtlı bir konumda tutulmalıdır. Atomic replace, ACL,
   symlink/reparse reddi, rollback ve backup recovery ayrıca doğrulanmalıdır.
   Bu PR disk registry loader'ı veya otomatik enrollment kurmaz. Enjekte edilen
   registry okuyucusu fresh, bağımsız ve güvenilir değilse kullanım BLOCKED'dır.
4. **Rotation:** Yeni ID + yeni fingerprint, yeni validity aralığı bağımsız
   ceremony ile kaydedilir. Eski ID aynı public key'e bağlı kalır; aynı ID'ye
   sessizce başka key atanmaz. Gerekli bounded overlap operatörce belirlenir;
   geçmiş grant expiry'si otomatik uzatılmaz. Yeni imza yeni ID kullanır.
5. **Revocation:** Key yetkisini kaldır/revoked yap, server revocation epoch'unu
   transaction ile artır, mevcut grant'ları iptal et; yeni incelemeler fresh
   registry/onay okumadan devam etmesin. İptal edilen key ile outstanding
   imzalar kabul edilmez. Review imzası, approval revocation'ını geçersiz kılamaz.
6. **Recovery:** Kayıp key, registry erişim hatası, çelişkili pin, rollback,
   epoch kaybı veya kimlik belirsizliğinde dur. Git/Copilot/PAT credentials,
   browser kimliği veya unsigned GitHub kaydıyla fallback yapma. Operator
   ceremony ve güvenilir audit/revision restore'u doğrulanmadan yeniden açma.
   Ham receipt, secret, task açıklaması veya private key audit'e yazılmaz.

### 4. Offline kanıt ve kalan kapılar

`npm run check` tüm mevcut relay testleriyle yeni adversarial testleri çalıştırır:
task/revision/actor/source uyuşmazlığı, forged reviewer/issuer, invalid signature,
mixed version, missing fields, malformed paths, revoked/expired key/approval,
out-of-order/nonce replay, aynı revision equivocation, rotation, stale cache,
partial outage, concurrent inspect ve cancellation. Testler gerçek authority,
GitHub, model, credential veya çalışan HIC servislerine bağlanmaz.

Canlı P1A.2 için authenticated actor ve snapshot route'u, mevcut ledger'da
revision/source/approval bağları, bağımsız key enrollment/storage/revocation ve
durable restart/rollback kanıtı **BLOCKED** kalır. P1B ayrıca #92 OS isolation,
canlı claim/lease, exact GitHub ref+CI attestation, SDK entitlement/quota ve
ayrı insan cost/tool onayı ister. Offline PASS bunların hiçbirini kaldırmaz.
