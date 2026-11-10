# ADR-079 (öneri): Gelecekte tenant-sınırlı Dashboard ve kişiselleştirme

## Yönetici Özeti

- **Durum:** Önerildi; kabul edilmiş karar değildir.
- **Tarih:** 2026-11-10; bağlam: Issue #79 Phase B.
- **Kapsam:** Yalnız tasarım. Login, tenancy, storage, extension runtime,
  migration veya executor bu PR'da uygulanmaz.
- **Öneri:** Önce sabit birinci-taraf paneller; sonra server-enforced
  tenant/user sınırı üzerinde sürümlü deklaratif layout ve capability manifest'i.
  Private extension varsayılandır; topluluğa terfi açık opt-in ve inceleme ister.
- İlgili belgeler: [Dashboard](dashboard-design.md), [API matrisi](api-mapping.md).

## Teknik Ek

### Bağlam ve değişmez sınırlar

Bugünkü local operator session bir cloud tenant kimliği değildir. Baseline
tasks/plans globaldir; UI filtrelemesi güvenlik sınırı olamaz. Phase A,
Control Plane task ID/state otoritesini ve ayrı plan/run/publication
provenance'ını korumalıdır. Local Podman/cihaz gelecekte cloud'un zorunlu
parçası olmamalı; kaynak adapter'ı taşınabilir read contract arkasında kalır.
Geçmiş onaylar yeni extension veya sürüme aktarılabilir genel yetki değildir.

### Seçenekler ve operasyon/maliyet etkisi

| Seçenek | Yarar | Risk / maliyet |
|---|---|---|
| A: Sabit birinci-taraf Dashboard | Phase A'da yeni servis/DB yok, en küçük attack surface | Kişiselleştirme yok; MVP için önerilir |
| B: Server-authorized deklaratif layout + registry | Paket yerine veri; sürüm/rollback ve kullanıcı izolasyonu | Login/policy/audit/storage işletimi; Phase B için öneri |
| C: Aynı-origin keyfi JS plugin | En geniş esneklik | Cookie/DOM/veri sızıntısı ve supply-chain riski; önerilmez |
| D: Her widget için remote servis | Bağımsız deploy | Ağ/auth/tenant routing ve maliyet yükü; kanıt olmadan erken |

DB seçimi, paylaşılan şema + RLS veya ayrı DB, kimlik sağlayıcısı ve sandbox
teknolojisi **açık sorudur**. AWS/Azure yönetilen kimlik/storage servisleri
ile sağlayıcıdan bağımsız seçenekler güvenlik, işletim ve güncel fiyatla
yeniden kıyaslanmalıdır. Bugün cloud fiyatı veya sağlayıcı seçilmez.

### Önerilen kimlik ve yetki sözleşmesi

Sunucu doğrulanmış login'den `tenantId,subjectId,roles,capabilities` üretir.
İstemcinin gönderdiği tenant/user ID yetki kaynağı olamaz. Her read/write,
list/detail, cache, audit, arama ve export aynı tenant+subject kontrolünü
uygular. Kaynak sahipliği DB sorgusunda doğrulanır; başka tenant ID'si,
nesne tahmini ve actor değişimi fail-closed olur. Authz context key'i
tenant+subject+resource+policyVersion içerir; kullanıcı değişince cache ve
in-flight istekler temizlenir. Birinci-taraf panel de muaf değildir.

Layout taslağı (yeni endpoint/DB şeması **değildir**):

```text
DashboardLayout:
  schemaVersion, layoutId, revision
  owner: tenantId + subjectId (sunucu otoritesi)
  panels: stablePanelId + widgetId + widgetVersion + placement + safeSettings
  updatedAt, previousRevision
```

Layout okuma/yazma exact revision/ETag ile yapılmalı; çakışma 409/412 ve açık
yeniden inceleme gerektirir, sessiz last-write-wins yok. Revision N'e rollback
yeni revision N+1 üretir; audit ve daha yeni capability iptalleri korunur.
Panel düzeni onay, prompt, credential, tool veya execution izni içermez.
Bozuk/unsupported widget sürümü karantinaya alınır; çekirdek Dashboard
güvenli sabit layout ile hata bildirerek açılabilir, yetki fallback'i yapamaz.
Kullanıcı/tenant arasında otomatik layout veya özel panel paylaşımı yok.

### Extension manifest'i ve güven sınırı (gelecek kontratı)

```text
ExtensionManifest:
  extensionId, version, manifestSchemaVersion, contentDigest, publisher
  compatibleDashboardVersions, panelIds, dataSchemaVersions
  requestedCapabilities, networkOrigins, settingsSchema
  isolationRequirements, rollbackTarget, distribution=private
```

Örnek capability adları öneridir: `tasks.read`, `decisions.read`,
`model.inventory.read`. Wildcard/network erişimi varsayılan kapalı.
Manifest'te yetki istemek yetki vermez; her tenant/user sürüm yükseltmesini
ve permission diff'ini inceler, sunucu policy'si en dar erişimi uygular.
`tasks.approve`, publish, lifecycle veya execution grant'i panel slotundan
türetilmez. Keyfi code extension, host shell, filesystem ve aynı-origin
cookie erişimi MVP'de yoktur. İleride code extension gerekirse ayrı origin,
dar şemalı message broker, CSP ve OS/network sınırları bağımsız inceleme
ister; iframe tek başına güvenilmeyen execution izolasyonu değildir.
Remote HTTP verisi de güvenilmezdir; HTML/script olarak render edilmez.

Private kurulum yalnız sahibinin HIC kapsamındadır. Topluluğa öner:
açık opt-in → sır/kişisel veri taraması → lisans/provenance kontrolü →
bağımsız güvenlik ve ürün incelemesi → sürümlü/sabit digest'li registry kaydı.
Hiçbir özel layout, memory, model ayarı veya credential pakete eklenmez.
Topluluğa kabul tüm kullanıcılara otomatik kurulum/izin vermez; kullanıcı
kurulumu ayrıca onaylar. Geri çekme, pin, quarantine ve rollback geçmişi
gerekir. Mevcut kullanıcı onayı yeni permission'a genişletilemez.

### Memory, model politikası ve mahremiyet

User-owned memory/model policy tenant+subject ile ayrılır; saklama süresi,
silme/export, hassas içerik redaction ve provider'a gönderim consent'i açık
olmalıdır. Model/provider tercihi bir UI layout'u değildir; ayrı versioned
policy ve bütçe/egress izinleri gerektirir. Secret yalnız sunucu secret
store/reference olarak kalır; browser/layout/extension'da tutulmaz.
Cross-tenant retrieval, training ve memory paylaşımı varsayılan reddedilir.
Policy revocation sonraki istekte uygulanır; eski cache/grant/rollback
iptali geri alamaz. Paid provider otomatik seçilemez.

### Kabul kapıları, ertelenen kararlar

Phase B öncesi: identity/login tehdit modeli; tenant enforcement tasarımı
(DB/cache/audit dahil); retention/deletion; capability ve extension signing/
provenance; revoke/rollback protokolü; AWS/Azure/portable maliyet karşılaştırması.
Negatif testler: tenant A list/detail/export'unda B nesnesi, forged tenant,
oturum değişiminde geç yanıt/cache, permission escalation, revoked grant,
manifest downgrade/tamper, layout revision conflict, private→community veri
sızıntısı, sandbox escape/egress ve model memory izolasyonu.

Bu testler burada koşulan tenant runtime testleri değildir; gelecek kabul
gereksinimleridir. Phase A yalnız sabit panel ID ve typed read adapter
sınırını korur; tenant kolonları, login servisi veya plugin loader eklemez.
Bu ADR kabul edilene kadar [mevcut kararlar](../../02-current-decisions.md)
değişmez. [English counterpart](../en/adr-079-personalization.md).
