# R2 — Kalıcı execution ownership ve fencing

## Yönetici Özeti

R2, mevcut Control Plane task engine'ini genişletir; ikinci engine yoktur.
PostgreSQL task satırı kilidi, kalıcı execution kaydı ve monoton generation
tek bir task için aynı anda bir owner kabul edilmesini hedefler. Redis yalnız
bildirim kuyruğudur. Lease, heartbeat, otomatik takeover ve recovery uygulanmaz.

Bu değişiklik **offline, dedicated PostgreSQL integration ve A2.2 izole
container E2E ile doğrulanmış uygulama adayıdır**. Aşağıdaki checkpoint'ler
kanıt kapsamını ve ölçüm sınırlarını ayırır; tam dayanıklılık veya exactly-once
external side effect garantisi vermez. Önceki R1/A1 sonuçları R2 kanıtı değildir.

## Teknik Ek

### Veri ve sözleşmeler

- `tasks.execution_generation`: kalıcı `bigint`, ilk değer 0.
- `task_executions`: `id` (ExecutionId), TaskId, WorkerId, generation, status,
  started/completed UTC zamanları. `(task_id, generation)` unique; tamamlanmamış
  execution için task başına partial unique index bulunur.
- `task_status_transitions.execution_id`: claim ve execution mutation audit
  kayıtlarını kalıcı execution'a bağlar.
- WorkerId process başına UUID; ExecutionId dequeued task işleme denemesi başına
  UUID'dir. Bunlar kimlik doğrulama credential'ı değildir.

`POST /tasks/{id}/executions/claim`:

```json
{"executionId":"11111111-1111-1111-1111-111111111111","workerId":"22222222-2222-2222-2222-222222222222"}
```

Yeni claim yanıtı:

```json
{"id":"33333333-3333-3333-3333-333333333333","status":"Executing","executionId":"11111111-1111-1111-1111-111111111111","workerId":"22222222-2222-2222-2222-222222222222","generation":1,"newlyClaimed":true}
```

Claim yalnız `Approved` veya `Retrying` için kabul edilir. Task kilidi altında
generation artırma, execution insert, `Executing` geçişi ve audit aynı
transaction içindedir. Hata hepsini rollback eder.

Aynı task/WorkerId/ExecutionId tekrarı mevcut kaydı `newlyClaimed:false` ile
döndürür; yeni audit, generation veya **yeni model çalıştırma izni üretmez**.
Tamamlanmış execution tekrarı da mevcut execution durumunu döndürür.
Farklı owner veya başka request'e ait ExecutionId: 409; olmayan task: 404;
boş UUID: 400. Belirsiz transport/commit sonucu başarı sayılmaz.

`POST /tasks/{id}/status` execution mutation örneği:

```json
{"status":"Completed","attemptCount":1,"result":"OK","errorMessage":null,"expectedStatus":"Executing","execution":{"executionId":"11111111-1111-1111-1111-111111111111","workerId":"22222222-2222-2222-2222-222222222222","generation":1}}
```

Owner, aktif execution ve task generation eşleşmeden mutation kabul edilmez.
200 yanıtı `id`, `status`, aynı `execution` proof'unu içerir. State machine
kuralları korunur; aynı durum tekrarı R1 gibi 409'dur.
`Executing` için generic status yolu kapalıdır; claim zorunludur.
`InProgress`/`Review` ara durumlarında aynı owner korunur. Bu aktif durumlardan
çıkışta execution kapanır; yeni bir execution için mevcut state machine
üzerinden yeniden `Approved`/`Retrying` durumuna gelmek gerekir.
Aktif owner varken approval/retry/status yolları da proof kontrolünü atlayamaz.
Owner'sız legacy execution mutation reddedilir; eski `Executing` satırlarına
kendiliğinden owner atanmaz.

### Worker güvenliği ve sınırlar

- Claim POST yalnız bir kez gönderilir; HTTP 200, doğru task/execution/worker,
  pozitif generation, `Executing` ve `newlyClaimed:true` birlikte doğrulanır.
- Replay, conflict, eksik/yanlış acknowledgement, timeout veya bilinmeyen sonuçta
  model çağrılmaz; otomatik claim retry veya yanıltıcı `Failed` yazımı yoktur.
- Model ve HTTP cancellation token'ı korunur; final mutation aynı proof'u taşır.
- Process restart sonrası PostgreSQL owner kaydı kalır. Yeni process yeni
  WorkerId ile otomatik resume/takeover yapamaz. Yanıt kaybı veya crash sonrası
  task askıda kalabilir; bu R3/R4 işidir.
- Fencing, harici model/API/file side effect'lerini exactly-once yapmaz.
  Model retry aynı execution içinde birden fazla provider çağrısı yapabilir.
- API henüz servis authentication/authorization sağlamaz; WorkerId assertion'dır.
  Bu sözleşme güvenilmeyen istemcilere açılmamalıdır.
- Redis destructive LPOP ve DB/queue dual-write pencereleri değişmedi.

### Açık migration ve rollout kapısı

Schema tanımı:
[002-execution-ownership.sql](../../agent-lab/control-plane/Migrations/002-execution-ownership.sql).
SQL uygulama tarafından **otomatik uygulanmaz**. API startup schema version 2
kaydını arar ve eksikse hata ile durur; schema kontrolünden önce base schema
initializer çalışmaz. SQL mevcut R1 `tasks`/`task_status_transitions` şemasını
gerektirir; boş veritabanı bootstrap aracı değildir.

Ayrı onay gerektiren deployment sırası:

1. Hedef/owner/backup ve migration rolü doğrulanır. Eski Worker'lar durdurulur;
   eski API ve doğrudan DB writer erişimleri de kapatılır. Aktif execution'lar
   insan kararıyla reconcile edilir; veri silinmez veya owner uydurulmaz.
2. Onaylı hedefte script **tek transaction** içinde uygulanır (örneğin
   `psql --single-transaction --set ON_ERROR_STOP=1 --file <migration>`).
   Version kaydı yalnız başarılı transaction ile kalıcılaşmalıdır.
3. R2 API ve R2 Worker koordineli dağıtılır; eski writer'ların geri dönmesi
   engellenir. Schema additive olsa da protokol değişikliği breaking'dir.
4. Güncel binary'lerle PostgreSQL concurrency/rollback ve isolated container
   E2E kanıtı alınır. Rollback için eski binary'leri doğrudan açmak güvenli
   değildir; ayrıca onaylı reconciliation/cutover planı gerekir.

Eski Worker yeni API üzerinden `/status:Executing` ile başlayamaz ve proof
olmadan tamamlayamaz. **Eski API veya doğrudan SQL writer** uygulama fencing'ini
atlayabilir; schema tek başına buna karşı koruma sağlamaz. Mixed-version rolling
deployment güvenli ilan edilmez. Bu görev hiçbir mevcut DB'yi migrate etmez.

### R3/R4 bağımlılıkları ve doğrulama

Gelecekte lease expiry/recovery kararı aynı task kilidi altında önceki owner'ı
kapatıp generation'ı monoton artırarak yeni execution oluşturmalıdır. Eski
identity/generation her mutation'da reddedilmeye devam etmelidir. Bu yalnız
tasarım bağımlılığıdır; R2'de expiry, heartbeat veya otomatik takeover yoktur.

Offline testler claim uygunluğu, identity/generation, replay eşleşmesi,
JSON sözleşmeleri ve Worker kabul/ret/cancellation/ambiguity davranışını kapsar.
Gerçek PostgreSQL test tanımları competing claim, replay, stale generation,
legacy bypass reddi, claim/finalization audit rollback ve kalıcılığı kapsar.
Bunlar yalnız bağımsız doğrulanmış dedicated fixture hedefinde çalıştırılmalıdır;
offline testler transaction/concurrency doğruluğunun yerini tutmaz.

[A2.1 staged harness](../../deploy/hic-runtime-test/README.md), dependencies
startup'ı, authoritative base schema + explicit v2 hazırlığını ve API startup'ını
ayırır. Her runtime/SQL işlemi ayrıca açık onay gerektirir; schema-version
kontrolü atlanmaz. Legacy `start` R2'de güvenli hata verir.

### PostgreSQL integration checkpoint — 6 Kasım 2026

- Solution restore ve Release build: PASS, 0 warning / 0 error.
- Yeni dedicated container: `hic-r2-pg-3d19de2f25dc`; volume:
  `hic-r2-pg-3d19de2f25dc-data`; internal ağ:
  `hic-r2-pg-3d19de2f25dc-net`; yalnız loopback `127.0.0.1:18433`.
  Mevcut runtime ve R1 test volume/veritabanları kullanılmadı.
- Fixture uyumluluğu için bu ayrı PostgreSQL sunucusunda database
  `hic_r1_tests`, owner/current role `hic_r1_test_runner`, database comment
  `HIC:R1:dedicated-integration:v1` kullanıldı. Rolün superuser, CREATEROLE,
  CREATEDB, replication ve BYPASSRLS yetkileri yok; credential repoya yazılmadı.
  Container/volume/network sahiplik etiketleri, mount ve port izolasyonu
  veritabanı kurulumundan önce doğrulandı.
- Schema v2 yalnız testin GUID schema'sına setup transaction'ı içinde
  uygulandı. Her fixture, DB/rol/marker ve schema/table OID doğrulaması yapar;
  cleanup lock + tam identity eşleşmesi + `RESTRICT` kullanır, `CASCADE` yoktur.
- PostgreSQL integration seçimi: **12 PASS, 0 fail / 0 skip** (4 R1, 8 R2).
  Gerçek assertion'lar: state/execution/audit commit; competing claim'de tek
  owner ve tek audit; generation `1 → 2`; eski identity/generation ile
  finalization reddi ve değişmeyen audit; farklı bağlantılarda kalıcı owner ve
  salt okunur replay; legacy bypass reddi; Review/InProgress boyunca owner'ın
  korunması; audit hatasında claim/generation ve finalization rollback.
- Tam Release suite:
  `dotnet test AI-Agent-Lab.sln -c Release --no-build --no-restore`:
  **119 PASS** (Control Plane 83, Worker 36), **0 fail / 0 skip**.
  Bunun 107'si offline testtir. Worker acknowledgement eşleşmesi, unknown claim
  sonucunda sıfır model çağrısı, tek claim POST ve cancellation kontrolleri
  deterministik HTTP/model fake'leriyle doğrulandı; gerçek HTTP/runtime
  concurrency kanıtı olarak sunulmaz.
- Her iki test çalıştırmasından sonra fixture schema sayısı `0`, public
  uygulama tablosu sayısı `0`. Mevcut 16 container'ın ID/image/start-stop
  zamanı/restart count/mount/port/network metadata'sı değişmedi; envantere yalnız
  dedicated container/volume/network eklendi. Runtime veri içeriklerinin
  kapsamlı karşılaştırması yapılmadı. Test kaynakları çalışır halde korundu;
  volume silinmedi.
- `git diff --check`: PASS. Test sonucu sayıları TRX counters üzerinden de
  doğrulandı. Testleri geçirmek için uygulama veya test mantığı değiştirilmedi.
- **Bu PostgreSQL checkpoint'i sırasında R2 container E2E: NOT RUN.**
  Sonraki runtime kanıtı aşağıdaki A2.2 kaydındadır. Gerçek LLM, canlı application DB migration,
  lease expiry, heartbeat, otomatik takeover veya recovery çalıştırılmadı.
  Exactly-once external side effect garantisi yoktur. R2 tamamen doğrulanmış
  release olarak kabul edilmemelidir.

### A2.2 izole runtime checkpoint — 6 Kasım 2026

#### Doğrulanan

- Kaynak HEAD: `a0ce873410ec575e64f99339370a5764b510c82d`;
  dal `feature/hic-reliability-r2`. A2.1 harness değişiklikleri henüz
  commit edilmemişti; uygulama/migration kaynakları değişmemişti.
- Tek yeni proje `hic-net10-4148a5c31de5`, API `127.0.0.1:18082`.
  Ayrı `hic-net10-4148a5c31de5_postgres-data` ve
  `hic-net10-4148a5c31de5_redis-data` volume'ları, internal
  `hic-net10-4148a5c31de5_test` ağı kullanıldı; DB/Redis host portu açılmadı.
- Staged `start-dependencies -> prepare-schema -> start-api` geçti.
  Authoritative initializer'dan okunan base SQL ve mevcut v2 migration yeni
  `hic_test` DB'sinde ayrı explicit transaction'larda uygulandı.
  Version `2`, beş tablo, base constraint/index'ler ve execution schema
  kolon/constraint/partial unique index kontrolleri geçti. API yalnız schema
  hazırlığından sonra; Worker yalnız primary task oluşturulup onaylandıktan sonra başladı.
- Primary TaskId `057c330f-3049-4abb-940c-9e23a9752ed0`;
  ExecutionId `5144bb66-181a-459d-9d45-7c8a3a01c6df`;
  WorkerId `8ff264f7-3b18-4cd2-b78a-9783c649cc93`.
  `Draft -> Approved -> Executing -> Completed`; sonuç `HIC_RUNTIME_TEST_OK`,
  attempt `1`, generation `1`, tek ve finalized execution. Üç geçişlik audit
  doğru; execution'a bağlı iki audit satırı mevcut.
- API-only safety TaskId `a69beebd-dddf-4c7d-ba4c-c27e77f987ad`, Redis'e
  gönderilmedi. Yeni claim `newlyClaimed:true`, replay `false`; iki conflicting
  claim ve üç invalid execution/worker/generation proof HTTP `409`.
  Retler state/audit'i değiştirmedi; geçerli owner ile `Completed` oldu.
  Bu API gözlemleri, ayrı PostgreSQL concurrent-claim testlerinin yerine geçmez.
- Son DB envanteri: iki task, iki kapalı execution, sıfır aktif execution,
  altı audit satırı. Redis queue/DLQ `0/0`; Worker loglarında bir dequeue,
  bir processed, sıfır failure diagnostic.
- Test sonunda beş servis running, volume/ağ/state korundu; otomatik stop veya
  cleanup yok. Önceki 17 container'ın ID/image/start-stop/restart/mount/port/ağ
  metadata'sı ve kaynak hash'leri değişmedi; önceki volume/ağ envanteri korundu.
  Mevcut DB'lere test yazımı yapılmadı; veri içeriklerinin kapsamlı karşılaştırması yok.

#### Runtime provenance

API/Worker önce cache kullanılmadan HEAD revision etiketiyle build edildi;
harness `start-api` sırasında yeniden build etti. Çalışan image'lar revision
etiketli image'larla aynı image/layer kimliği olarak sunulmaz. Her iki çalışan
ana DLL'nin SHA-256 değeri fresh revision-etiketli build'deki DLL ile birebir eşleşti.

| Servis | Çalışan image ID | Ana DLL SHA-256 |
|---|---|---|
| API | `de9f96ea8a47bf156e3ed482ef94db92031c48c3aae5c9b4a88c276aec31293d` | `d0c4e338273b390209ac29c99a5f3836a687ecfa395c9aac39ce68fd797fd75b` |
| Worker | `6e491d1cd1fba45015228fb94d60289d51ce077c104c1b67942c96f835ff83b0` | `c2f4984b9c0f9f2089f80131de1d814a5fb08a16aa10bc7433302b1d3a52ca94` |

#### Çıkarım, ölçülmeyen ve çalıştırılmayan

- **Çıkarım:** Primary Worker'ın `newlyClaimed:true` kabulü, doğrulanmış binary'nin
  kabul kapısı ve başarılı execution üzerinden çıkarılır; raw claim HTTP response
  bağımsız yakalanmadı. Doğrudan true/false gözlemi safety task'ında alındı.
- **NOT MEASURED:** Bağımsız model HTTP invocation sayısı; stub sayaç sunmuyor.
  Stub-only endpoint, boş model anahtarı ve internal ağ doğrulandı; bu ölçümler
  kesin bir model çağrısı veya kapsamlı network-traffic capture iddiası değildir.
- **NOT RUN / NOT VERIFIED:** Runtime crash recovery, lease expiry/takeover,
  destructive fault injection, gerçek/cloud LLM ve exactly-once external side effects.
  Lease/heartbeat/recovery uygulanmadı; R3/R4 ve Bridge kapsam dışı kaldı.
- A2.2'de static validation ve 34 offline harness testi tekrar geçti;
  önceki 119/119 Release suite ve 12/12 PG testi yeniden çalıştırılmadı.
  A2.3 checkpoint hazırlığında aynı static/34 offline test ve whitespace
  kontrolleri yeniden geçti; runtime veya SQL işlemi çalıştırılmadı.
