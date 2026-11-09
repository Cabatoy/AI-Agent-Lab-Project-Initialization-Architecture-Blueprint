# HIC — Manifest doğrulayıcı, faz 1

## Yönetici özeti

Bu bağımsız kütüphane mantıksal input/output manifestlerini ve sağlanan byte
dizilerini doğrular. Sandbox, staging, Worker bağlantısı veya yürütme yetkisi
sağlamaz. Çıktı manifesti güvenilmezdir; kendi hash'i insan onayı değildir.
Bağımsız güvenlik incelemesi ve ayrı entegrasyon kararı gerekir.

## Teknik ek

`ArtifactInputManifest` ve `ArtifactOutputManifest`, `fields` ve
`manifestSha256` içerir. Hash yalnız `fields` nesnesinin
`CanonicalJson.Serialize` çıktısının SHA-256 değeridir; envelope hash'e dahil
değildir. JSON UTF-8, en fazla 16 KiB ve 16 seviye derinliktedir. Aynı alanın
tekrarı, bilinmeyen veya eksik alan, null collection öğesi ve güvensiz metin
reddedilir. Array sırası hash'e dahildir.

Güvenilir çağıran, input alanlarının **tamamını** yetkili kontrol düzleminden
elde edip `VerifyInput` için `trusted` olarak sağlar. Doğrulayıcı her alanı
canonical digest ile karşılaştırır; task, run, approval, repository,
installation, base, source tree, image, operasyon, kaynak girdileri ve policy
değişimi farklı digest üretir. `VerifyOutput`, aynı güvenilir input ve
doğrulanmış input digest'ine bağlanır. Çağıran önce `VerifyInput` sonucunu
doğrulamalı, yetki ve iptal durumunu ayrıca kontrol etmelidir. Manifestten
okunan değerleri kendilerine karşılaştırmak güven ilişkisi oluşturmaz.

Kural kümesi exact, büyük/küçük harfe duyarlı çıktı yollarını ve her yol için
önceden güvenilir olarak sağlanmış izinli SHA-256 değerlerini içerir. **Bütün**
kuralların birer dosya ile karşılanması zorunludur; boşluk, eksik, ek veya
silinmiş çıktı kabul edilmez. Hash önceden bilinmeyen üretken çıktı için bu
faz uygun değildir: yetki ve byte incelemesi için ayrı tasarım gerekir.
Maksimum 100 dosya, dosya başına 1 MiB, toplam 8 MiB ve policy içindeki daha
düşük sınırlar uygulanır. Hash ve boyut sağlanan byte'larla karşılaştırılır.
Yalnız `regular` ve `100644` kabul edilir.

Yollar platformdan bağımsız ASCII göreli biçimdedir: `/` ayırıcı, en fazla
16 segment, 240 karakter; dot/dot-dot, boş segment, Windows device adları,
`.git`, ters eğik çizgi, sürücü, UNC, ADS, kontrol karakteri, trailing dot
ve space reddedilir. Unicode isimler alias riskini azaltmak için bu fazda
reddedilir. Case-insensitive tekrarlar ve dosya/dizin prefix çakışmaları
reddedilir. Örnek: `src/message.txt` uygun bir path biçimidir; `../message.txt`,
`C:/message.txt`, `.GIT/config`, `NUL.txt` ve `src/name.` `INVALID_PATH` verir.
İzinli olmayan bir geçerli yol `PATH_NOT_ALLOWED`, farklı içerik
`CONTENT_MISMATCH`, yanlış bağ `OUTPUT_BINDING_MISMATCH` verir.

Test byte dizileri sentetiktir. Kütüphane filesystem okumaz; arşiv açma,
symlink/hardlink tespiti, openat/no-follow, TOCTOU, eşzamanlı byte mutasyonu,
network/secret isolation, log denetimi, iptal ve GitHub publishing **test
edilmemiştir**. Source tree digest alanı yetkili beklenen değere bağlanır;
tree'nin gerçek bytes ile eşleşmesi bu kütüphanede doğrulanmaz. Entegrasyonda
çağıran doğrulanmış byte'ların değiştirilememesini ve güvenli taşınmasını
sağlamalıdır. Bu kısıtlar hostile-code isolation kanıtı değildir.
