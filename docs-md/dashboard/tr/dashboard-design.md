# HIC #79 — Operator Dashboard tasarım önerisi

## Yönetici Özeti

**Durum: öneri; uygulama veya ürün kararı olarak onaylanmadı.** Bu dal yalnız
belge ve offline sözleşme testi içerir. Dashboard yeni task state machine
değildir. İlk dilim mevcut Overview, Tasks, Plans ve tanı bileşenlerinden
salt okunur bir giriş sayfası üretmeyi hedefler; bu dal UI'yı değiştirmez.
Worker, executor, servis lifecycle, migration, merge/deploy ve ücretli model
çağrısı kapsam dışıdır. Kişiselleştirme yalnız [önerilen ADR](adr-079-personalization.md)
ile ele alınır; tenant runtime'ı teslim edilmiş değildir.

**Kaynaklar:** develop `bcfa97679e235f75483135d19a64654228fecc27`;
#59 `0601242279d3a55bd39a2fc278ea5c0df429cf0e` → #61
`c8df5725beb981bb3e9438d44a366dcae7899ccb` → #66
`ff4337578df67b1905e52abd664b5548efcbc9de` → #73
`07a49f9d511ad7dfd55b9f258f759568061e7046`. Hepsi inceleme
snapshot'ıdır; açık Draft PR, mevcut kullanıcı runtime'ına uygulanmış sayılmaz.
#76/#78 kaynak politikası `fa8dd6e1f78fd1d6dee3d241d9c336d576e41e74`
ayrı daldadır. Bu tasarım `develop` tabanındadır; zinciri merge etmez.

### Bilgi mimarisi ve öncelik

| Alan / contract ID | İçerik ve davranış | Dilim |
|---|---|---|
| Görevler — `tasks` | Gerçek task durum sayıları, son işler, Tasks detayına bağlantı | A1 |
| Planlar — `plans` | Mevcut execution plan sayısı ve Plans bağlantısı; general plan ile karıştırma | A1 |
| Sağlık — `health` | Control Plane / PostgreSQL / Redis; Worker kontrol edilmedi | A1 |
| Kararlar — `decisions` | Seçili task için actor'a bağlı karar geçmişi, revision ve provenance; global inbox değil | A2, bağımlı |
| GitHub — `github` | Yapılandırılmış hedef, bilinen publication referansı ve açık issue önizlemesi; CI feed yok | A2, bağımlı |
| Model — `model` | Oturumla yapılandırılmış yerel model envanter kontrolü; inference garantisi değil | A2, bağımlı |
| Kaynaklar — `resources` | Web-safe API yok; “Henüz bağlanmadı”, tarihsel RAM canlı gösterilmez | A3, eksik |
| Kullanım/maliyet — `usage` | Doğrulanmış token/fiyat telemetrisi yok; sayı veya tasarruf uydurma | A3, eksik |
| Ajanlar — `agents` | Kalıcı registry yok; sıfır ajan/çalışıyor varsayımı yok | Gelecek |

Navigasyon önerisi: Dashboard (mevcut Overview'ın ilerideki evrimi), Tasks,
Plans, Conversations, Planning, Local Runner ve Isolated Git. Var olan
workflow'lar korunur; ikinci editör veya onay sistemi yaratılmaz.
Agents / Models & Providers / Approvals, develop ile stacked UI'da farklı
olduğundan özellik kullanılabilirliği capability ve sürümle doğrulanır.
Üst şerit: aktif bağlam, oturum durumu, salt okunur sınır, son başarılı
**panel bazlı** zaman ve Yenile. “Bekleyen insan kararı”, `Draft`, `Review`
veya `Approved` task durumlarından hesaplanmaz.

## Teknik Ek

### Wireframe ve responsive sözleşme

Önerilen masaüstü (≥1200 CSS px):

```text
Nav | Dashboard · bağlam · oturum · Salt okunur · Yenile
    | Hata/stale bildirimi (ilgili panelle ilişkilendirilmiş)
    | Görev özeti       | Plan özeti       | Sağlık
    | Son görevler (2 sütun)              | Kararlar / detay
    | GitHub            | Model            | Kaynaklar / kullanım
```

768–1199: iki sütun, daraltılabilir menü, seçili detay listenin altında.
320–767: tek sütun; menü düğmesi/drawer, tüm içerik doğal DOM sırasında;
detay tam genişlikte. Öncelik: hata/oturum, görevler, kararlar, sağlık,
planlar, GitHub, model, kaynaklar, kullanım. Drawer kapandığında odak açan
düğmeye döner; Escape kapatır. Masaüstünde görsel yerleşim DOM/klavye
sırasını değiştirmez. 200% zoom ve 320 px reflow'da sayfa yatay taşmaz.
Uzun SHA/repository ve güvenilmeyen içerik sarılır; dar ekranda task satırı
etiketli karta dönüşür, teknik diff için yalnız kendi kapsayıcısı kayabilir.

- `main`, `nav`, tek `h1`, başlıklı bölümler ve skip link; mevcut
  [bileşenler](../../../src/WebUI/src/components.tsx) korunacak yeniden kullanım yüzeyidir.
- Görünür klavye odağı; renge ek metin/ikon; normal metinde en az 4.5:1
  kontrast; önerilen minimum dokunma hedefi 44×44 CSS px.
- Yükleme ve yenileme `role=status`/polite; hatalar `role=alert`;
  periyodik sayaç her değişimde ekran okuyucuyu susturmayacak şekilde özetlenir.
- Detay açılışında başlığa odak, kapanışta kaynak satıra dönüş; destructive
  veya onay düğmesi varsayılan odak olamaz. Reduced-motion desteklenir.
- Bu metin browser kabul kanıtı değildir; uygulama aşamasında gerçek
  320/768/1200/1440 viewport ve klavye/ekran okuyucu kontrolleri gerekir.

### Otorite, durum ve boşluk anlamları

[API matrisi](api-mapping.md) method, yetki ve yan etkiyi ayrı kaydeder.
`tasks` tablosu task otoritesidir; local run durumu ve publication state'i
ayrı rozetlerde kalır. `InProgress`/`Executing` çalışan task grubudur;
`Approved`/`Retrying` çalışan Worker kanıtı değildir. Bilinmeyen yeni task
durumu ham etiketiyle “Bilinmeyen durum” gösterilir, sessizce Completed olmaz.
`DeadLetter` endpoint/status'u yok; kutu çizilmez ve sıfır gösterilmez.

Her panel: `loading`, `empty`, `ready`, `stale`, `offline`, `unauthenticated`,
`forbidden`, `invalid-contract`, `unavailable`. Başarılı `[]` gerçek boş;
timeout/401/403/503/bozuk JSON sayı 0 değildir. Eski son başarılı veri yalnız
“Eski — zaman” etiketiyle tutulabilir; auth/actor değişiminde özel veri hemen
silinir. Kısmi hata sağlıklı diğer panelleri gizlemez; `/health` başarısı
DB/Redis/model/Worker sağlıklı demek değildir.

### Yenileme ve oturum önerisi

- Yalnız A1 dört GET'i: 30 saniyede en çok bir çevrim, en çok 2 eşzamanlı
  istek ve çevrimde toplam 4 istek; her istek 8 saniye timeout. Otomatik
  retry yok; hata sonrası sonraki çevrim veya açık Yenile. 60 saniye sonra
  veri stale. Yenile düğmesi devam eden çevrimle birleştirilir.
- `AbortController`: unmount, hidden, offline, session-ended ve context-change
  durumunda iptal. Generation ID geç yanıtın yeni bağlamı ezmesini engeller.
  Görünür/online dönüşte tek birleşik çevrim; gizliyken polling yok.
- A1 listeleri bugün **sayfasız**; en fazla 50 satır render etmek yanıtın
  ağ/bellek sınırı değildir. Tam başarılı listenin durumları sayılır ve
  “dönen görevler” olarak etiketlenir. Büyük kurulumlar için pagination +
  otoriter aggregation ayrı backend kapısıdır; `?limit=` uydurulmaz.
- Stacked/provider okuması otomatik polling'e eklenmez; açık panel/istek,
  geçerli oturum ve kabul edilmiş sözleşme gerekir. Her task için history
  fan-out yok; yalnız seçili task. GET session restore tek sefer, login
  otomatik yapılmaz. 401 tüm özel panelleri temizler; 403 erişim engeli.
- HttpOnly, SameSite=Strict cookie/proxy davranışı yeniden kullanılır;
  token URL, localStorage, analytics, log, hata veya export'a konulmaz.
  Mevcut client helper dış iptal sinyali almıyor: bunu eklemek gelecekteki
  UI diliminin testli gereksinimidir, mevcut davranış diye sunulmaz.

### İnsan kararının UX sınırı

İlk dilim yalnız ilgili mevcut review sayfasına yönlendirir; Dashboard
mount/refresh, preview oluşturmaz, approve/reject veya import göndermez.
Gelecekte inbox: task/conversation ID, actor, action/scope, exact revision,
digest, kaynak zamanı, expiry, server-issued plan ve karar kaydı gösterir.
İncele → exact diff/plan → ayrı açık Onayla/Reddet adımı; toplu onay yok.
Task/revision/actor/repository/digest değişince eski inceleme geçersizdir.
401/409/expired ve sonucu belirsiz POST'ta otomatik tekrar yok; önce
durable history okunur. Global pending listesi ve tek tip provenance
henüz yok; tamamlanmış karar geçmişi “bekleyen inbox” gibi sunulamaz.
Git onayı run rezervasyonu oluşturabilir ama execution/publish izni değildir;
general plan onayı yalnız Draft üretir. Eski runtime gate'i varsayılmaz.

### Dilimler, test planı ve kabul kapıları

1. **A0 (bu PR):** IA/API/responsive önerisi, gelecek ADR, offline belge
   sözleşmeleri; runtime ve browser kabul iddiası yok.
2. **A1 (ayrı onay/PR):** mevcut Overview/Tasks/Plans/tanı yeniden kullanımı;
   typed payload, partial failure, unknown≠zero, cancel/late-response,
   offline/hidden, duplicate refresh testleri.
3. **A2:** #59/#61/#66/#73 kabulü, deployed capability/schema kontrolü,
   oturum/actor/revision negatif testleri; eski registry ve global inbox
   eksikleri açık kalır. #73 ayrı yetkilendirme/read-only incelemesi olmadan
   GitHub Dashboard entegrasyonu yapılmaz; publisher yetkisi verilmez.
4. **A3:** ayrı onaylı read-only resource köprüsü ve doğrulanmış usage/cost
   telemetrisi. Shell/Podman/WSL çağrısı browser'dan yapılmaz; host,
   WSL/cgroup, container ve process ölçümleri toplanmaz.
5. **Kabul:** exact-head CI + gerçek browser viewport/keyboard testi +
   operator geri bildirimi. Test matrisi: 200 `[]`, bozuk JSON, timeout,
   401/403/404/409/503, geç yanıt, değişen actor/revision, `hasMore=true`,
   private issue, yetkisiz repo, bulunmayan telemetry. Hiçbir test paylaşılan
   preview'a veya gerçek DB'ye erişmez; sonraki implementation testleri
   sentetik fixture/HTTP mock kullanır, ayrı izin olmadan lifecycle yapmaz.

Maliyet/operasyon: A1 yeni servis, paket veya model çağrısı gerektirmemeli.
Polling yükü yukarıdaki bütçeyle sınırlı önerilir; enerji/RAM tasarrufu
ölçülmedi. A2 geniş liste fan-out'u ve broker privilege maliyeti nedeniyle
ertelemelidir. A3 adapter'ı local cihazı zorunlu cloud bağımlılığı yapmamalı.
AWS/Azure kimlik, storage ve fiyat seçimi yapılmadı; sağlayıcı karşılaştırması
ve göç yeniden değerlendirilecek açık konudur.

Makine-okunur [tasarım sözleşmesi](../dashboard-contract.json) yalnız test
fixture'ıdır; uygulama tarafından yüklenmez. Offline belge testini
`src/WebUI` dizininden `npm test -- tests/dashboard-design.test.ts` ile çalıştırın;
bu test gerçek UI/polling/authz davranışını doğrulamaz. Kaynak yolları ayrıca
sabit Git SHA'larındaki route declaration'larıyla karşılaştırılmıştır.
[İngilizce eş](../en/dashboard-design.md).
