# HIC — Karantinaya alınmış artifact çekirdeği

## Yönetici özeti

Bu çalışma #69'un `2c0455f8155720f0c252185097eb5586b1bb8993`
commit'ine bağlıdır; #70'in güvenli bir bölümünü uygular, sandbox veya
publisher entegrasyonu değildir. Diskten hostile veri alma, kalıcı staging,
gerçek insan kabulü, execution ve GitHub yayını henüz desteklenmez.
Güvenli filesystem backend'i yokken giriş açıkça
`UNSAFE_FILESYSTEM_BACKEND` ile reddedilir.

## Teknik ek

`QuarantinedArtifacts.Import`, çağıranın güvenilir bir kaynaktan **önceden**
elde ettiği byte dizileri ile #69'un trusted input manifestini alır. Input
manifest ve kaynak byte'ları #69 verifier ile yeniden doğrulanır. Çıktı
yolları input policy kapsamına, sayıya, boyuta, tipe ve moda bağlanır;
byte dizisi kopyalanarak uzunluk ve SHA-256 doğrulanır. Bağ ve sıralanmış
dosya metadata'sı üzerinde domain-ayrımlı candidate digest hesaplanır.
Üretken çıktı, #69'un preapproved-hash kurallarını gevşetmeden karantinada
gözlenebilir. Dönen byte dizileri kopyadır; caller belleğindeki sonraki
değişiklik candidate içeriğini değiştirmez.

`Accept`, yalnızca caller tarafından **bağımsız güvenilir otoriteden**
sağlanmış, ilk plan kararından farklı bir karar ID'si, tam bağ ve candidate
digest'i eşleştiğinde bellek içi `AcceptedArtifact` döndürür. Bu API kararın
gerçek, geçerli, iptal edilmemiş veya insan tarafından verilmiş olduğunu
kanıtlamaz; veritabanı veya audit bağlantısı yoktur. Bu nesne mevcut publisher
tarafından kullanılmaz ve kendi başına publication yetkisi değildir. Tekrar
çağrılar saf ve deterministiktir; durable replay/claim kontrolü yoktur.
Entegrasyon öncesi Control Plane'den atomik, güncel yetki kontrolü, audit ve
ayrı publication onayı zorunludur.

Test yalnız testin oluşturduğu geçici dizinde hash adlı dosyaya yazar,
değiştirilmiş byte'ı yeniden import eder ve reddi gözlemler. Bu bir
production storage backend'i, dosyanın fiziksel değişmezliği veya filesystem
yarışlarına karşı kanıt değildir. Uygulama tarafından güvenli content-addressed
disk staging henüz uygulanmadı. `CandidateDiff.Preview` yalnız caller'ın
bağımsız doğruladığı kaynak hash'iyle eşleşen byte'ları karşılaştırır; UTF-8
ve 64 KiB girdi, toplam 8 KiB değişiklik sınırı uygular. Tek bir bounded
değişiklik aralığı sunar; kapsamlı patch, sanitizasyon veya onay değildir.
İçeriği hassas veri içerebileceğinden loglanmamalıdır. Timeout, cancellation,
log limitleri, source tree doğrulaması ve kalıcı handoff kapsam dışıdır.

| Güvenlik kapısı | Durum | Kanıt / sınır |
|---|---|---|
| Boyut, digest, path, mod, duplicate, scope | PASS | Sentetik unit testleri; içerik kopyası ve bağımsız SHA kontrolü |
| Başka run/karar/digest ile acceptance replay | PASS (saf çekirdek) | Eşleşme testleri; durable replay kontrolü değildir |
| Hostile dizinden okuma | PASS (fail-closed) | `UNSAFE_FILESYSTEM_BACKEND`; dosya açılmaz |
| Bounded diff önizlemesi | PASS | Bellekte UTF-8, digest ve boyut sınırları; patch veya onay değildir |
| Kalıcı content-addressed staging | NOT TESTED | Production backend uygulanmadı; yalnız test fixture yazımı |
| Symlink/reparse, mount, cross-device ve rename yarışları | NOT TESTED | Gerçek OS yarışları çalıştırılmadı; pre-check güvenlik kanıtı değildir |
| Ağ, sırlar, mount ve kaynak izolasyonu | NOT TESTED | Disposable izole runner üzerinde henüz doğrulanmadı |
| Gerçek karar, audit, publisher ve retry | NOT TESTED | Entegrasyon yok; hiçbir yayın denenmedi |
| Bağımsız insan güvenlik incelemesi | NOT TESTED | Entegrasyon öncesi zorunlu |

İleride Linux için `openat2` veya descriptor-relative `O_NOFOLLOW` ile
mount/ownership kısıtları; Windows için handle-relative açma ve reparse
doğrulaması, capability detection ve fail-closed davranışı ayrı ayrı
tasarlanıp adversarial test edilmelidir. `Path.GetFullPath`,
`File.GetAttributes`, `CheckLinks` veya `NoLinks` TOCTOU koruması değildir.
Worker, migration ve güvenilmeyen execution bu değişiklikle etkinleşmez.
