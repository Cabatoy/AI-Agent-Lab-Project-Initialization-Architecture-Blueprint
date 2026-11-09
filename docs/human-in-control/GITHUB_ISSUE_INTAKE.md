# HIC — GitHub Issue intake hazırlığı

## Yönetici özeti

Bu değişiklik yalnız issue referansı ve kaynak snapshot doğrulama çekirdeğidir;
operator arayüzü, GitHub'dan okuma ve Draft task oluşturma **uygulanmamıştır**.
Issue metni güvenilmeyen veridir; onay, yürütme veya yayın yetkisi değildir.
Worker, execution, migration ve publisher davranışı değiştirilmez.

## Teknik ek

`GitHubIssueIntake`, yalnız yetkili olarak sağlanmış repository ile eşleşen
GitHub issue URL veya `owner/repo/#number` biçimini kabul eder. Canonical
kaynak; repository ve issue sayısal kimlikleri, numara, normalize metin,
GitHub güncelleme zamanı ve SHA-256 digest içerir. Metin 16 KiB, başlık
512 byte ile sınırlandırılır. İçerik, talimat olarak değil ham güvenilmeyen
veri olarak korunur ve hiçbir log veya model istemine otomatik eklenmez.

Bu saf çekirdek GitHub'a bağlanmaz ve çağıranın sağladığı repository kimliği
ve issue içeriğinin doğruluğunu bağımsız olarak kanıtlamaz. GitHub App broker
mevcut yayın senaryosu için izinleri kesin olarak sınırlar; issue okuma yetkisi
mevcut token sözleşmesinde yoktur. Yetki genişletme kullanıcı onayı ve ayrı
güvenlik incelemesi olmadan yapılmamalıdır.

İlk gerçek milestone için repository-scoped read-only issue erişiminin
onaylanması, güvenilir backend'den doğrulanmış kaynak, operator oturumu,
revision bazlı durable dedup ve açık insan onaylı Draft oluşturma gerekir.
Schema değişikliği varsa yedek, izole PostgreSQL testleri ve canlı migration
için ayrı açık onay gereklidir. Prompt injection, eşzamanlı import, restart,
revizyon ve iptal testleri ile gerçek UI kanıtı henüz yoktur. Mevcut kod
herhangi bir endpoint veya modele bağlanmadığından milestone tamamlanmış
sayılmaz.
