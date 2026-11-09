# HIC — GitHub Issue intake hazırlığı

## Yönetici özeti

Bu değişiklik doğrulama çekirdeği, issue önizlemesi ve açık insan onayıyla
Draft task oluşturma akışını içerir. Mevcut GitHub App kurulumunda yalnız
yetkili, **public** repository issue'ları desteklenir; private issue
okuma **uygulanmamıştır**.
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
onayı ve ayrı güvenlik incelemesi olmadan eklenmemelidir.

Operatör başlık ve açıklamayı düzenler, önce tam Draft önizlemesini görür,
ardından ayrı düğmeyle onaylar. Sunucu önizleme ve onayda issue'yu yeniden
okuyarak revision ve repository kimliğini karşılaştırır; eski revision
reddedilir. Beş dakikalık, actor'a bağlı değiştirilemez preview ve tekil
repository/issue/revision kaydı mevcut `tasks` tablosundaki Draft ile aynı
transaction içinde saklanır. Aynı revision tekrarı mevcut task'ı döndürür;
yeni revision ayrı inceleme ve onay gerektirir. Revoked/expired operator
oturumu commit noktasında tekrar doğrulanır. Import hiçbir run veya GitHub
yayını oluşturmaz ve metni modele otomatik göndermez. GitHub'daki son okuma
ile yerel transaction commit'i atomik değildir; issue bu aralıkta değişebilir.
UI proxy yalnız bu üç issue endpoint'ini, aynı origin ve operator oturumu
kontrolleriyle iletir; confirm boş gövde dışında girdi kabul etmez.

Manuel `011-github-issue-import.sql` migration'ı yalnız yedek ve ayrı açık
onayla uygulanabilir; startup sırasında çalıştırılmaz. Migration yoksa import
503 ile kapanır; issue önizlemesi kullanılabilir. İzole sentetik PostgreSQL ve
HTTP testleri gerçek operatör onayı, gerçek GitHub veya browser kanıtı değildir.
Oturumlar süreçte tutulduğundan API restart sonrası yeniden login gerekir;
durable import audit ve dedup veritabanındadır. Runtime limitleri, özel issue
erişimi ve bağımsız güvenlik incelemesi ayrı kapılardır.
