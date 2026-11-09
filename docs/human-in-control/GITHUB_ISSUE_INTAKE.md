# HIC — GitHub Issue intake hazırlığı

## Yönetici özeti

Bu değişiklik doğrulama çekirdeği ve operator için salt okunur issue
önizlemesidir. Mevcut GitHub App kurulumunda yalnız yetkili, **public**
repository issue'ları görüntülenir; private issue ve Draft task oluşturma
**uygulanmamıştır**.
Issue metni güvenilmeyen veridir; onay, yürütme veya yayın yetkisi değildir.
Worker, execution, migration ve publisher davranışı değiştirilmez.

## Teknik ek

`GitHubIssueIntake`, yalnız yetkili olarak sağlanmış repository ile eşleşen
GitHub issue URL veya `owner/repo/#number` biçimini kabul eder. Canonical
kaynak; repository ve issue sayısal kimlikleri, numara, normalize metin,
GitHub güncelleme zamanı ve SHA-256 digest içerir. Metin 16 KiB, başlık
512 byte ile sınırlandırılır. İçerik, talimat olarak değil ham güvenilmeyen
veri olarak korunur ve hiçbir log veya model istemine otomatik eklenmez.

Önizleme endpoint'i operator oturumunu doğrular, mevcut GitHub App broker ile
kurulu repository kimliğini doğrular, yalnız public repository'ye GET yapar ve
yanıt öncesinde oturumu yeniden kontrol eder. Private repository için issue
isteği göndermeden fail-closed döner. Mevcut broker yayın için kapsamlandırılmış
token kullanır; endpoint yalnız GET gönderir, ayrı read-only credential broker
henüz yoktur. Yetkiler genişletilmez ve private issue okuma yetkisi kullanıcı
onayı ve ayrı güvenlik incelemesi olmadan eklenmemelidir. Önizleme kalıcı değildir.

İlk gerçek milestone için repository-scoped read-only issue erişiminin
onaylanması, güvenilir backend'den doğrulanmış kaynak, operator oturumu,
revision bazlı durable dedup ve açık insan onaylı Draft oluşturma gerekir.
Schema değişikliği varsa yedek, izole PostgreSQL testleri ve canlı migration
için ayrı açık onay gereklidir. Prompt injection, eşzamanlı import, restart,
revizyon ve iptal testleri ile gerçek UI kanıtı henüz yoktur. Mevcut kod
herhangi bir endpoint veya modele bağlanmadığından milestone tamamlanmış
sayılmaz.
