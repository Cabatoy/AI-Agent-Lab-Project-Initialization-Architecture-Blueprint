# HIC ajan yürütmesi: tehdit modeli ve izolasyon planı

## Yönetici Özeti

**Durum: güvenlik kapısı açık; uygulama ve release hazır değil.** #42 için
önerilen en küçük sınır, Control Plane'in mevcut onay ve durable run kayıtlarını
kullanarak her run için ayrı, rootless Podman container oluşturmasıdır. Kod ve
testler ağsız container içinde çalışır; GitHub yayını güvenilir Control Plane'de
kalır. Bu belge mimari öneridir, production için onaylanmış karar değildir.

Windows Podman VM üzerinde ağsız, host mount'u olmayan, ayrıcalıkları düşürülmüş
container ve sentetik offline derleme test edilmiştir. Ancak bu VM'de `memory`,
`pids` ve `cpu` cgroup controller'ları kullanılamamaktadır. Kaynak sınırları
uygulanamadığından bu profil production backend olarak etkinleştirilmemeli;
kontrol sağlanamıyorsa hata verilmeli, host çalıştırmasına dönüş yapılmamalıdır.
Mevcut #43 ve #51 host yürütme yolları bu sınırı henüz sağlamaz.

## Teknik Ek

### 1. Tehdit aktörleri ve sınırlar

- Güvenilmeyen repo yazarı ve model çıktısı; dosya adları, Git içeriği, testler,
  build tanımları, çıktı ve loglar dahil tüm girdileri kontrol edebilir.
- Host üzerinde eşzamanlı repo veya çalışma dizini değiştirebilen aktör, kontrol
  ile kullanım arasında symlink, junction veya rename/swap kullanabilir.
- Control Plane, onaylı plan, broker ve doğrulayıcı güvenilir alandır; container,
  kaynak repo ve dışa aktarılan tüm byte'lar güvenilmezdir. Podman yöneticisi,
  container'a API/socket vermeyen ayrı bir kontrol yüzeyidir.
- Aynı işletim sistemi kullanıcısı Control Plane process'ini veya kimlik
  bilgilerini doğrudan değiştirebiliyorsa container tek başına buna karşı
  koruma sağlamaz. Buna karşı ayrı servis kimliği ve işletim sistemi erişim
  kontrolleri gerekir. Host admin, VM yöneticisi ve kernel güvenilir varsayılır.

Mevcut Git runner host'ta path kontrolünden sonra worktree ve dosya açar; bu
kontroller atomik değildir. Model runner host `dotnet build` çalıştırır. #54
publisher ayrıca test edilmiş içeriği host worktree yolundan tekrar okur. Statik
reparse kontrolü, kaynak checkout'unun bozulmamış görünmesi veya container
içindeki test sonucu bu yarışları tek başına kapatmaz.

### 2. Seçenekler ve önerilen sınır

| Backend | Artı | Sınır |
|---|---|---|
| Host process/worktree | Mevcut, basit | Host credential ve TOCTOU sınırı yok; uygun değil. |
| Rootless Podman, run başına container | Mevcut VM; mount yok, ağ kapalı, cgroup ve namespace uygulanabilir | Ortak Linux kernel/VM; kernel ve container runtime kaçış riski var; controller'lar doğrulanmalı. |
| Run başına ayrı VM/microVM | Daha güçlü kernel sınırı | Provisioning, maliyet ve operasyon daha yüksek; mevcut local akışta kanıtlanmadı. |

Önerilen ilk adım: digest ile sabitlenmiş, güvenilir kaynaklı Linux image;
run başına ayrı container, ayrı kullanıcı, `--network none`, read-only root,
yalnız boyut sınırlı özel geçici çalışma alanı, tüm capability'ler düşürülmüş,
`no-new-privileges`, desteklenen varsayılan seccomp ve AppArmor/SELinux,
çekirdek/VM desteğine göre CPU, bellek, PID, disk, süre ve çıktı sınırları.
Her mekanizmanın **gerçekte uygulandığı** doğrulanmalı. `--privileged`, host
network/PID, host bind mount, runtime socket, credential, inherited environment
ve host'a yazılabilir paylaşım yasak. Konfigürasyon veya denetim başarısızsa
run başarısızdır; daha zayıf profile otomatik geçiş yoktur.

Container, Podman VM'nin kernel'ini paylaşır; güvenlik açığı ve DoS riski
sıfırlanmaz. Windows/WSL2 dosya paylaşımı, VM ve rootless user namespace
detayları farklı olabilir; yerel cgroup delegation yoksa VM/servis
konfigürasyonu ayrıca insan onayıyla düzeltilmeli veya başka backend seçilmelidir.
Mevcut VM ayarları bu çalışma kapsamında değiştirilmedi.

### 3. Uygulama sözleşmesi ve aşamalar

1. Claim ve actor/task/plan digest onayı mevcut Control Plane transaction ve
   audit üzerinden alınır; ikinci task state machine oluşturulmaz.
2. Güvenilir, ayrıcalığı sınırlı importer onaylı repository kimliği ve base
   commit'i doğrular. Host repo'ya bind mount verilmez. Immutable Git object
   kaynaklarından dosyaları güvenli biçimde okuyup onaylı commit/tree ile
   doğrulamak; symlink, submodule, special file, traversal, case/Unicode
   çakışması, boyut ve dosya sayısı sınırlarında fail-closed davranmak gerekir.
   Salt path kontrolünü izleyen host dosya okuması güvenli snapshot değildir.
3. Doğrulanan içerik özel container storage'a kopyalanır; snapshot digest'i,
   image digest'i, plan, test politikası ve run kimliği audit ile bağlanır.
   Gizli bilgiler, model credential'ları ve Podman API container'a aktarılmaz.
4. Onaylı edit ve test yalnız container'da çalışır. Worker'ın ağ, filesystem,
   süresi, process sayısı ve log çıktısı sınırlandırılır. Agent çıktısı, exit
   code ve test raporu yetkisiz başarı iddiası olarak değil, doğrulanacak veri
   olarak ele alınır.
5. İşlem durdurulduktan sonra sınırlı boyutlu, normal dosya içeriği ve manifest
   dışa aktarılır; link, özel dosya ve kapsam dışı yollar reddedilir. Güvenilir
   Control Plane byte digest'ini hesaplayıp **onaylı plan içeriği ve test edilmiş
   artifact ile eşitliği** doğrular. #54 publisher mevcut worktree path okuması
   yerine bu değişmez artifact referansını tüketmeli; ayrı GitHub onayı,
   repository identity ve reconciliation korunmalıdır.
6. İptalde container sonlandırılır ve run/audit sonucu açıkça kaydedilir.
   Crash sonrası run ID ve sahiplik etiketleriyle yalnız o run'a ait kaynak
   bulunur; dış yazı belirsizse otomatik tekrar yapılmaz, mevcut reconciliation
   kullanılır. Cleanup yalnız sahipliği kanıtlanmış disposable kaynağı hedefler;
   mevcut volume ve servisler dokunulmaz.

### 4. Kanıt ve eksikler

[Podman izolasyon provası](../../../tests/security-contracts/podman-isolation-proof.cjs), mevcut image ID'siyle
sentetik container çalıştırır. Non-root UID, sıfır effective capability,
no-new-privileges, host ortamındaki sentetik değişkenin aktarılmaması, iki
bilinen socket yolunun ve iki bilinen Windows yolunun yokluğu, yalnız loopback,
HTTP denemesinin başarısızlığı, read-only root, container içi symlink/swap ve
offline sentetik derleme kontrol edildi. Bunlar tam sızma testi, mevcut HIC
entegrasyonu, tüm socket/yol/credential keşfi veya hostile host rename testi
değildir. Image ID yalnız bu makinedeki test fixture'ıdır; güvenilir ve
sabitlenmiş production image politikası değildir.

Windows junction ve alt yolunun mevcut guard tarafından reddi #43'te test
edildi; Windows symlink oluşturma yetkisi mevcut değildi. PostgreSQL SIGKILL
testinde ayrı test DB'de claim/audit dayanıklılığı ve açık transaction rollback
gözlendi; gerçek yayın crash'i ve container cancellation/recovery kanıtlanmadı.
#54 mock testleri duplicate ve yayın reconciliation davranışını kapsar; yeni
backend/artifact ile henüz test edilmedi.

**Merge öncesi:** uygun izole VM ve etkili resource controller'ları; güvenli
snapshot/import/export; #43/#51/#54 entegrasyonu ve güvenli cancellation;
gerçek host eşzamanlı swap, credential/socket, ağ, traversal/junction,
artifact tampering ve crash saldırı testleri; tam platform/CI kanıtı; bağımsız
insan güvenlik incelemesi gereklidir. Mevcut başarılı CI veya sentetik prova
bu gereksinimlerin yerine geçmez.
