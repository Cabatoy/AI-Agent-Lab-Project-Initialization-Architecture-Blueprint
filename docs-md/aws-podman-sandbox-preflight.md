# HIC — AWS Rootless Podman Sandbox Preflight

## Yönetici Özeti

Bu çalışma, gelecekteki Linux rootless Podman yürütme sınırını değerlendirmek
için varsayılan olarak kapalı, offline-first bir preflight sözleşmesidir.
Kanıtları fail-closed değerlendirir; container başlatmaz, ajan tarafından
üretilmiş kod çalıştırmaz, AWS kaynağı oluşturmaz ve HIC task'ını onaylamaz.
`AgentCodeExecutionAllowed` her zaman `false` değerindedir.

Uygulama manifest/kanıt doğrulaması, temizlenmiş üç durumlu sonuç ve salt
okunur Podman denetleyicisi, açıkça yetkilendirilmiş sentetik probe ve güvenilir
kanıt doğrulayıcısı için arayüzler sağlar. Gerçek runtime adapter'ı yoktur.
Birim testleri sahte kanıt ve sahte doğrulayıcı kullanır; başarılı offline test
Podman, cgroup, AWS veya host izolasyonunu kanıtlamaz.

## Teknik Ek

### Mevcut uygulama sınırı

- [RootlessPodmanSandboxPreflight.cs](../src/NativeHost/RootlessPodmanSandboxPreflight.cs)
  değişmez manifesti, sabit güvenlik politikasını, üç durumlu kanıt
  sözleşmesini, arayüzleri, digest doğrulamasını, tazelik/bağlama kontrollerini,
  replay ret sözleşmesini ve temizlenmiş raporu tanımlar.
- `IReadOnlyPodmanInspector`, `IAuthorizedSyntheticPodmanProbe` ve
  `ITrustedSandboxEvidenceVerifier` yalnızca sözleşmedir. `podman`, shell,
  container veya ağ isteği başlatan bir implementasyon yoktur.
- Control Plane, Worker, task durumu, onay endpoint'i, execution ledger,
  launcher veya Relay yolu bu bileşene bağlanmamıştır. Bileşen ikinci bir HIC
  task state machine'i veya execution grant'i oluşturmaz.
- Manifest değişmez image/input digest'lerini, açık ve pozitif CPU, bellek,
  PID, timeout ve çıktı limitlerini, sabit göreli çıktı kökünü, challenge
  nonce'unu, policy digest'ini, oluşturulma/sona erme zamanını ve kanonik
  digest'ini taşır. Değişken image tag'lerini, değiştirilmiş/eski manifestleri
  ve ajan kodu çalıştırmaya izin veren manifestleri reddeder.
- Kanıt tam manifest, probe ID, nonce ve image digest ile eşleşmeli, taze
  olmalı, enjekte edilen güvenilir doğrulayıcıdan geçmeli ve her kontrol için
  gerekli runtime ölçümü ile negatif test kanıt kodlarını taşımalıdır. Yalnızca
  CLI yapılandırma/flag kanıtı kontrolü geçiremez.
- Sinyaller güvenilir doğrulayıcının manifest limitleriyle karşılaştıracağı
  sınırlı ham ölçüm taşıyabilir. Bu ölçüm rapora kopyalanmaz ve bu bileşen
  tarafından loglanmaz; üretim doğrulayıcısı yalnızca kanıt kodlarını değil,
  sayısal cgroup/runtime değerlerini de doğrulamalıdır.
- Üretim doğrulayıcısı kanıtın gerçek kaynağını, challenge'ı, manifest
  bağını, gerçek cgroup/runtime ölçümlerini ve negatif testleri doğrulamalıdır.
  Bu slice'ta yerleşik doğrulayıcı veya kendini doğrulama kestirmesi yoktur.

### Kanıt durumları

| Durum | Anlamı |
|---|---|
| `verified` | Gelecekte bağımsız runtime doğrulaması için ayrılmış durumdur; bu offline slice, doğrulanmış görünen kontrolleri `unverified` durumuna indirir ve bu sonucu üretmez. |
| `blocked` | Gelecekte bağımsız olarak doğrulanmış runtime başarısızlığı veya açık güvenlik politikası engeli için ayrılmıştır; bu offline slice bu durumu üretmez. |
| `unverified` | Kanıt eksik, geçersiz, reddedilmiş, replay, eski, çelişkili veya bağımsız doğrulanmamışsa; doğrulayıcı yoksa veya hata verirse döner. Execution başlamaz. |

Doğrulayıcı hatası yalnızca `trusted-verifier-error` sabit koduyla raporlanır;
exception ayrıntıları rapora eklenmez. Replay sonucu da yürütmeye izin vermez.

Temizlenmiş raporda yalnızca probe ID, geçerli image/manifest digest'leri,
kontrol başına durum, sabit neden kodları ve
`AgentCodeExecutionAllowed=false` bulunur. Ham ölçüm, ortam değeri, mount yolu,
credential, socket adı ve sağlayıcıya özgü kaynak ID'si raporlanmaz.

### Gerekli kontroller

Kanıt sözleşmesi rootless Podman ve user namespace davranışını; non-root kimliği;
CPU, bellek ve PID cgroup zorlamasını; salt okunur root ve staged input'u;
sınırlı çıktıyı; capability kaldırma, no-new-privileges ve seccomp'ı; network
namespace, outbound, DNS ve cloud metadata engelini; ortam/credential izolasyonunu; SSH/cloud
credential'ları ve container socket'lerinin dışlanmasını; host/komşu worktree
ve home izolasyonunu; symlink/path traversal engelini; tam sahiplikli sınırlı
temizliği; timeout ve çıktı limitlerini; değişmez image digest'i ve manifest
doğrulamasını ayrı ayrı kapsar.

Kontrollerin çoğu tanımlı runtime ölçümünü ve ilgili negatif testi gerektirir;
cleanup sahiplik makbuzu ile negatif test, image/manifest ise digest
doğrulaması ile runtime ölçümü gerektirir. Bu kodların gerçek gözlemlere karşılık geldiğini,
çağıranın beyanı olmadığını enjekte edilen doğrulayıcı kanıtlamalıdır.
Mevcut slice'ta bağımsız runtime doğrulaması bulunmadığından, sözleşmedeki
kanıtlar tam olsa bile kontroller `verified` olarak raporlanmaz.

### Offline test kanıtı ve sınırları

Operatörün bu son güvenlik düzeltmelerinden önce AWS çalışma alanında bağımsız
olarak bildirdiği odaklı .NET test sonucu: derleme başarılı (4,2 saniye);
**23 başarılı, 0 başarısız, 0
atlanmış** (test süresi 2,0 saniye). Bu sonuç offline sözleşme testlerine
aittir; yeni düzeltmeleri veya gerçek runtime izolasyonunu doğrulamaz.

Odaklı .NET testleri eksik/sahte/replay kanıtı, eski ve uyuşmayan manifest,
yalnız CLI flag iddiası, yazılabilir input/output/workspace kanıtı,
credential/socket maruziyeti, network/DNS erişilebilirliği, path traversal,
yinelenen gözlem, eksik cgroup kanıtı, tamamlanmamış cleanup ve temizlenmiş
çıktıyı sınar. Sahte doğrulayıcıyla kabul edilen sonuç yalnızca doğrulama ve
toplama kodunu test eder; gerçek bir özelliği `verified` yapmaz.

Gerçek Podman probe'u önerilmedi veya çalıştırılmadı. Rootless/user namespace
çalışması, cgroup delegasyonu/zorlaması, dosya sistemi/mount kısıtları,
seccomp, network/DNS engeli, host credential izolasyonu, cleanup ve AWS host
hazırlığı **UNVERIFIED** durumundadır. Bu uygulamada gerçek ortamdan ölçüm
bulunmadığı için hiçbir runtime özelliği **BLOCKED** olarak raporlanmamıştır.

### Kalan kapılar

**Issue #144**

1. Kodu, policy'yi, kanonik serileştirmeyi ve kanıt doğrulayıcı sözleşmesini
   bağımsız olarak gözden geçir.
2. Güvenilir doğrulayıcı ve güvenli rootless-Podman gözlemcisi geliştirip
   incele; kanıtı bağımsız doğrulanmış host, challenge, tam manifest ve tazelik
   aralığına bağla.
3. Herhangi bir Podman probe'undan önce operatör onayı al. Yalnızca sentetik,
   atılabilir fixture'ı kişisel olarak yönetilen AWS EC2 host'unda kullan;
   EC2/IAM/network/SSH/SSM yapılandırmasını veya çalışan HIC kaynaklarını
   değiştirme.
4. Gerçek cgroup quota/throttling, memory/OOM, PID limitleri, salt okunur
   mount'lar, network/DNS engeli, credential/socket, traversal, replay,
   timeout, çıktı sınırı ve tam sahiplikli cleanup dahil kontrolleri ve
   adversarial negatif testleri ölç. Eksikleri blocked/unverified olarak kaydet.
5. Gelecekteki executor entegrasyonunu değerlendirmeden önce bağımsız güvenlik
   incelemesi ve artifact/output karantinası ekle.

**Issue #153**

- Ajan kodu çalıştırma ve unattended Relay modu kapalı kalır.
- Başarılı preflight tek başına onay değildir. #153; #144 runtime kapıları
  bağımsız doğrulanana ve #153'ün kendi authoritative Control Plane onay,
  claim/replay, iptal/kurtarma, artifact, credential ve operatör rızası
  kapıları gözden geçirilene kadar blocked kalır.
- Production execution, model/tool çağrısı, yayınlama, merge ve deploy bu
  slice'ın dışındadır ve ayrı onay gerektirir.

Execution claim'in tek kaynağı mevcut Control Plane authoritative ledger'ıdır.
Duplicate execution önleme, onay, task durumu ve audit orada uygulanmalıdır;
preflight bileşeninde yerel task/run state deposu bulunmaz.
