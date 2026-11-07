# Workspace Refactor CI: v3 Çalıştırmadan Doğrulama

## Yönetici Özeti

Bu refactor'un doğrulama sınırı PostgreSQL schema v2'dir. Dört mevcut ledger
workflow'undaki v3 integration işleri, yalnız aynı depodaki
`refactor/enterprise-workspace-layout-20261107-223711` dalından açılan PR ve
bu dalın doğrudan `workflow_dispatch` çağrıları için çalıştırılmaz. Başka
dalların mevcut ledger işleri ve manuel tetikleme davranışı korunur.

**V3 testleri bu refactor için çalıştırılmamıştır.** Atlanan bir GitHub job'u
başarılı required check gibi değerlendirilebileceğinden, bu durum v3 testlerinin
geçtiğine veya PR'ın merge'e hazır olduğuna kanıt değildir. Bu görevde merge
yapılmamalıdır.

## Teknik Ek

- Dört workflow, sınırlı dal koşulunun yanında, checkout sonrasında ve herhangi
  bir database/container komutundan önce `refuse-v3-on-layout.sh` kontrolünü
  çalıştırır. Dal/repo kimliği eksikse veya beklenmeyen bir event gelirse bu
  kontrol açık hata ile durur.
- Bu PR için `hic-layout-only-ci.yml`, base commit'in head içinde bulunduğunu,
  beş taşınan projenin kaynak ve migration içeriklerini, test sözleşmelerini ve
  v2 bootstrap dosyalarını Git blob'ları üzerinden karşılaştırır. Test proje
  referanslarındaki beklenen yol değişiklikleri ve sentetik input'un tek yol
  düzeltmesi dışında değişiklik kabul edilmez. Kapsam genişlerse guard başarısız
  olur; v3 işi buna rağmen başlatılmaz.
- Guard'ın sentetik Git testleri, değişen migration, Native Host kaynağı, test
  projesi ve ledger sözleşmesinin reddedildiğini doğrular. Ayrı testler, dört
  workflow koşulunu ve PR/manual-dispatch shell kontrollerini inceler; database
  veya Worker başlatmaz.
- V2 için normal build, offline test, runtime-boundary kontrolü ve mevcut
  disposable managed-runtime CI ayrı sürer. V3 ledger kontrolleri başka PR'lar
  için değiştirilmez; bu istisna yalnız belirtilen refactor dalına yöneliktir.
- Bu kontrol, kötü niyetli workflow değişikliklerine karşı bağımsız bir güvenlik
  mekanizması veya v3 davranış testi değildir. PR incelemesi, açık onay kapıları
  ve v3 kapsamına ilişkin sonraki ayrı karar gereklidir.
