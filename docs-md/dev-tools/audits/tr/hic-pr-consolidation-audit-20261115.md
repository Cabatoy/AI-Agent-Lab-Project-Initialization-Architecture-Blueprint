# HIC dal ve PR konsolidasyon denetimi

**Snapshot:** 15 Kasım 2026, yaklaşık 22:30–22:34 UTC+03. Canlı GitHub
envanteri 46 açık PR döndürdü; 46'sı da Draft. Bu belge öneridir; merge,
retarget, kapatma, deploy veya çalışma zamanı izni değildir.
[English summary](../en/hic-pr-consolidation-audit-20261115.md).

## Yönetici özeti

- **Aktif yol:** #94 → #96 → #98 → #118 → #121 → #125 → #145 → #162;
  paralelde #145'ten türemiş #156 → #158 → #160. Oklar bağımlılığı
  gösterir, doğrulanmış merge onayını değil. #96'nın güncel commit'i #98'de
  yok; #162 de #156'da yok. Bu iki boşluk kapatılmadan zincir bütün değildir.
- #160'ın PR açıklaması yalnız dokümantasyon ve `7c7e67b...` diyor. Gerçek
  HEAD `15222274...`: #158 üzerinde **49 commit**, 11 dosya, Control Plane
  endpoint/store, migration ve testler içeriyor. Açıklama ve eski test kanıtı
  güncel HEAD için yeterli değildir.
- #156'da `control-plane` başarısız: AgentBridge
  `OfflineCliEmitsApprovalPreviewThenVerifiedReceipt`, `STALE_SNAPSHOT`.
  #145 ve #156'da GitGuardian geçmişteki iki işaret nedeniyle başarısız.
  #145 açıklaması bunları sentetik/false positive olarak sınıflandırıyor;
  güvenlik sahibi triage ve geçerli CI sonucu hâlâ gereklidir.
- #162, #158 ve #160 HEAD'lerinde yalnız GitGuardian sonucu görüldü. Yeşil
  tek kontrol, gerekli Relay/.NET/UI testlerinin çalıştığı anlamına gelmez.
  #132'de üç, #43'te bir kontrol `SKIPPED` olarak raporlandı.
- **Korunan referanslar:** #162 HEAD `6a7e50ee928c96f28d407e7530d644b3b33e06a9`
  ve çalışan AWS Control Plane'in henüz bağımsız doğrulanmamış revizyonu.
  İkisi de değiştirilmez; bilinmeyen runtime SHA tahmin edilmez.
- Hiçbir PR bugün yalnız bu envantere dayanarak “merge için güvenli” değildir:
  hepsi Draft; insan incelemesi, gerekli check'ler ve bağımlılık/koruma
  politikaları ayrıca değerlendirilmelidir.

## Teknik ek: yöntem ve sınırlar

GitHub açık PR'ları, her PR'ın base/head referansları, tam HEAD SHA'sı,
commit sayısı ve HEAD check sonuçları GraphQL ile alındı. İlgili PR açıklama,
yorum ve Git commit ancestry bilgileri ayrıca karşılaştırıldı. Kontroller
snapshot'tır; commit sayısı GitHub'ın PR karşılaştırma sayısıdır. Yeşil
check bile çalışmayan bir workflow'u veya gerekli review'ü kanıtlamaz.
AWS API/SSH, canlı container, Scheduler ve Relay watcher sorgulanmadı;
mevcut kaynaklara dokunulmadı.

### Canlı PR envanteri

`G/K/A/B`: başarılı / başarısız / atlanan veya nötr / diğer check sayısı.
Her PR bağlantısının **Commits** ve **Checks** sekmeleri tek tek commit ve
check ayrıntısının kaynağıdır. Tüm satırlar Draft'tır.

| PR | Base | Head | Tam HEAD | Commit | G/K/A/B |
|---|---|---|---|---:|---|
| [#162](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/162) | `feature/issue-142-relay-auth-rate-limit` | `fix/hic-issue142-windows-repair` | `6a7e50ee928c96f28d407e7530d644b3b33e06a9` | 1 | 1/0/0/0 |
| [#160](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/160) | `feature/hic-157-staging-observations-ui` | `feature/hic-159-relay-handoff-contract` | `15222274e389e81ac473d7d7195383a0208c31b1` | 49 | 1/0/0/0 |
| [#158](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/158) | `feature/hic-relay-aws-staging-contract` | `feature/hic-157-staging-observations-ui` | `90576bcab788d3da0a25398db9e0995b3a9b43cf` | 4 | 1/0/0/0 |
| [#156](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/156) | `develop` | `feature/hic-relay-aws-staging-contract` | `d4ca15fb33a9e3e148025f78d74601fe43ff2b1b` | 58 | 9/2/0/0 |
| [#155](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/155) | `develop` | `docs/hic-product-readme-refresh-20261115` | `e390367510bf37221968cea4b24332650d5bb6b2` | 1 | 1/0/0/0 |
| [#154](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/154) | `main` | `feature/issue-144-aws-sandbox-preflight` | `631a7c09df1378987d6fa6001847dec029129fce` | 52 | 8/0/0/0 |
| [#145](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/145) | `develop` | `feature/issue-142-relay-auth-rate-limit` | `b95bec235a1634bfed0c38079b0fff52ce32007b` | 41 | 3/1/0/0 |
| [#143](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/143) | `develop` | `task/hic-relay-smoke-approval-141` | `d06c22cf0cff5f36a56c4d05cdfb84d2b196940a` | 1 | 1/0/0/0 |
| [#140](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/140) | `develop` | `task/hic-relay-smoke-139` | `6803519dbe671e57ca860768741d102dec6fbab2` | 1 | 1/0/0/0 |
| [#138](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/138) | `task/128-operator-approval-draft` | `task/137-operator-approval-ui` | `846dac44baeaf6d64aa7e8a7edc3dad9b47fe97e` | 1 | 1/0/0/0 |
| [#132](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/132) | `develop` | `task/128-operator-approval-draft` | `61aecb975a4695adb660716e8cdd87ecd801a4b0` | 5 | 12/0/3/0 |
| [#127](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/127) | `develop` | `docs/issue-126-relay-draft-pr-note` | `410d5cce047aecd5c4360d9c7f85906d945b74e1` | 1 | 1/0/0/0 |
| [#125](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/125) | `feature/hic-relay-watch-polling` | `feature/hic-relay-agent-mode` | `059a2b841b5f53c7af55119c8cb824ed8a743982` | 1 | 1/0/0/0 |
| [#121](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/121) | `feature/hic-relay-vscode-chat-issue` | `feature/hic-relay-watch-polling` | `3f071311d9521e25873059492b4a79a46b50b79e` | 9 | 1/0/0/0 |
| [#118](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/118) | `feature/hic-dev-ack-channel` | `feature/hic-relay-vscode-chat-issue` | `8da2e23825a4d854c03c6c98c8eff4dac62a6714` | 3 | 3/0/0/0 |
| [#117](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/117) | `feature/hic-workspace-ux-v1` | `docs/hic-first-local-chat-116` | `e6345ac939437f0b6f91e9f175e3a4b86539e616` | 1 | 1/0/0/0 |
| [#114](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/114) | `develop` | `feature/hic-oci-terraform-plan-only-112` | `595f252599beba46001abb3d4baf0a3de2e7e009` | 4 | 2/0/0/0 |
| [#113](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/113) | `develop` | `feature/hic-issue106-offline-provider` | `c06cb6ffa950a33d05f4d30d2fe97bc091045f7e` | 1 | 4/0/0/0 |
| [#111](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/111) | `develop` | `feature/hic-oci-cloudshell-readonly-preflight-107` | `739ffb96859499ff5bd0db68069594ddb1c45480` | 4 | 2/0/0/0 |
| [#110](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/110) | `develop` | `docs/hic-provider-neutral-sandbox-design-105` | `3b49bb4c1c1250e4d5759aafd01d2406b1d21cf4` | 4 | 1/0/0/0 |
| [#103](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/103) | `feature/hic-dev-signed-review-p1a` | `feature/issue-102-read-only-authority` | `0f8c89fd467abd04fef805dc1bb6675fe796d179` | 1 | 3/0/0/0 |
| [#101](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/101) | `feature/hic-dev-ack-channel` | `feature/hic-dev-signed-review-p1a` | `7cb0118a29e40f71e86e0096cb2cf98e28a857ee` | 5 | 3/0/0/0 |
| [#98](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/98) | `feature/hic-dev-proposal-watcher` | `feature/hic-dev-ack-channel` | `abfefdaa31065c34a2cd15058f39cc07207f22f3` | 15 | 3/0/0/0 |
| [#96](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/96) | `docs/hic-dev-work-order-protocol` | `feature/hic-dev-proposal-watcher` | `508e04b93d26e7700a11d1e38d954ce02b3699c6` | 7 | 3/0/0/0 |
| [#95](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/95) | `feature/issue-90-copilot-sdk-s2-readiness` | `feature/issue-92-copilot-sdk-offline-sandbox` | `0f1d3567f47e1e97fd04bc91a18cf360003cf527` | 1 | 3/0/0/0 |
| [#94](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/94) | `develop` | `docs/hic-dev-work-order-protocol` | `04ccdef1b1233224ed881d82fc03a65a3aa33ae6` | 2 | 1/0/0/0 |
| [#91](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/91) | `feature/issue-88-copilot-sdk-poc` | `feature/issue-90-copilot-sdk-s2-readiness` | `96061108334922f8528c3cb57700934bb29edc31` | 8 | 3/0/0/0 |
| [#89](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/89) | `develop` | `feature/issue-88-copilot-sdk-poc` | `06082dd402047232733d27f8e13fdd92abd6d067` | 1 | 4/0/0/0 |
| [#86](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/86) | `develop` | `feature/hic-fusion-handoff-d0-d1` | `cca925a652bf8e0c1e8d64a81642613054d0acdf` | 4 | 6/0/0/0 |
| [#85](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/85) | `hic-github-issue-intake` | `docs/hic-issue-reader-boundary` | `273a813b93d1f6c9849c5fa7612d7bdd9403c920` | 1 | 1/0/0/0 |
| [#84](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/84) | `docs/hic-operator-dashboard-design` | `feature/hic-dashboard-a1` | `57125cdd85ed058782ab90838d94b2e1e3114287` | 1 | 7/0/0/0 |
| [#81](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/81) | `develop` | `docs/hic-operator-dashboard-design` | `4bd84bff7848341f1a3adaebe3617fe8338e33c5` | 1 | 9/0/0/0 |
| [#78](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/78) | `feature/hic-resource-profiles` | `feature/hic-resource-phase2` | `fa8dd6e1f78fd1d6dee3d241d9c336d576e41e74` | 2 | 7/0/0/0 |
| [#77](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/77) | `develop` | `feature/hic-resource-profiles` | `2a6b56a09672bf34ccd681958c634c394192b357` | 3 | 9/0/0/0 |
| [#73](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/73) | `feature/hic-git-plan-decisions` | `hic-github-issue-intake` | `07a49f9d511ad7dfd55b9f258f759568061e7046` | 7 | 19/0/0/0 |
| [#71](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/71) | `feature/hic-artifact-manifest-verifier` | `hic-artifact-quarantine` | `5b639e9b111c5c507a09502a4a0848a227c89636` | 2 | 1/0/0/0 |
| [#69](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/69) | `develop` | `feature/hic-artifact-manifest-verifier` | `2c0455f8155720f0c252185097eb5586b1bb8993` | 1 | 8/0/0/0 |
| [#68](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/68) | `develop` | `docs/hic-artifact-boundary-v1` | `2da0f26775472f009d989895cfd25336f076b7ff` | 1 | 1/0/0/0 |
| [#66](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/66) | `feature/hic-draft-execution-boundary` | `feature/hic-git-plan-decisions` | `ff4337578df67b1905e52abd664b5548efcbc9de` | 1 | 1/0/0/0 |
| [#65](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/65) | `develop` | `experiment/hic-cgroup-stress-evidence` | `6ad609aac0bb2b8b380a530d40bb98c1e04b31d0` | 6 | 2/0/0/0 |
| [#64](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/64) | `develop` | `docs/hic-extension-research` | `7fcb4985d3ec2da6f4f930baa93b15a6f35a8e5e` | 1 | 1/0/0/0 |
| [#61](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/61) | `feature/hic-workspace-ux-v1` | `feature/hic-draft-execution-boundary` | `c8df5725beb981bb3e9438d44a366dcae7899ccb` | 1 | 1/0/0/0 |
| [#59](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/59) | `feature/hic-github-publisher` | `feature/hic-workspace-ux-v1` | `0601242279d3a55bd39a2fc278ea5c0df429cf0e` | 5 | 1/0/0/0 |
| [#54](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/54) | `feature/hic-chat-approved-code-edit-pr` | `feature/hic-github-publisher` | `d41ba78b11f4378af55cfc4c14cd36b62dd8f674` | 8 | 1/0/0/0 |
| [#51](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/51) | `feature/hic-approved-isolated-git` | `feature/hic-chat-approved-code-edit-pr` | `ea945f379a2a7ed995399aa3a5e5189b81dd2898` | 2 | 1/0/0/0 |
| [#43](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/43) | `develop` | `feature/hic-approved-isolated-git` | `3e8e7376ddeaa3a4ff92f9c456bab6fff055c6e0` | 7 | 10/0/1/0 |

### Bağımlılık ve kapsam karşılaştırması

1. **Relay:** #118 → #121 → #125 SHA ancestry ile doğrulandı; #125 HEAD'i
   #145'in atası, #145 HEAD'i #162'nin doğrudan ebeveyni. #145 bu eski
   değişiklikleri içerir; dolayısıyla alternatif bir **tek konsolidasyon PR'ı**
   seçilirse #118/#121/#125 potansiyel mükerrer adaylardır. Ayrı inceleme
   birimleri olarak hâlâ anlamlı olabilirler; hiçbirinin kapanması öneri
   olmaktan öte karara bağlanmış değildir.
2. #96 HEAD `508e04b...`, #98'in dayandığı `afcfa726...` noktasından bir
   commit ileride; o commit Relay CI base filtresini değiştirir. #98, #145
   ve dolayısıyla korunan #162 bu güncel #96 HEAD'ini ancestry ile içermez.
   Önce eksik commit'in sonucu ve CI filtreleri incelenmeli, sonra insanın
   seçeceği non-destructive entegrasyon yöntemi uygulanmalıdır.
3. **AWS staging:** #145 HEAD'i #156'nın atası, fakat #162 HEAD'i değildir:
   iki dal #145'te ayrılıyor. #156 → #158 → #160 ancestry doğrudur. #156
   mevcutta `develop` tabanına karşı 58 commit/51 dosya içerir; yalnız yeni
   staging değişikliği gibi değerlendirilmemelidir. #158 proxy allowlist'i
   içeren kısmi UI aşamasıdır; PR açıklaması UI/test işlerinin kalan kısmını
   açıkça sayar. #160 artık yalnız ADR değildir; migration ve handoff API'si
   dahil tüm diff yeniden gözden geçirilmelidir.
4. **Authorization:** #132 → #138 gerçek stacked zincirdir; #138 tek commit
   ile operatör onayı UI'sını ekler. Bunlar birbirinin alternatifi değildir.
   Task approve yolu Worker kuyruğuna yazabildiğinden, observe-only Relay
   smoke için yeniden kullanılmamalı; #132/#138 otomatik önkoşul sayılmamalıdır.
5. **AWS sandbox #154:** GitHub ve yerel remote-tracking ref HEAD'i
   `631a7c09df1378987d6fa6001847dec029129fce`, base `main`. Yorumdaki
   operatör checkpoint'i `2d6cf0920be3f8acbfd4a39c3920470df0b7bd33`
   bu HEAD'in atasıdır; arada 50 commit vardır. Bu tarihsel fark, mevcut
   AWS checkout'unun SHA'sı veya bir Git divergence kanıtı değildir. Canlı
   AWS checkout'u incelenmedi. #154 HEAD'i #156 veya #160'ın atası değildir;
   karşılıklı dal karşılaştırması ve entegrasyon kararı gerekir. #154'teki
   AgentBridge fixture düzeltmesi #156/#160 ancestry'sinde de yoktur; bunun
   aktarılmasının ilgili başarısızlığı düzelttiği henüz test edilmedi.
6. Diğer doğrudan zincirler: #89 → #91 → #95; #98 → #101 → #103;
   #81 → #84; #77 → #78; #69 → #71; #43 → #51 → #54 → #59 →
   #61 → #66 → #73 → #85; #59 → #117. #51, #43'ün güncel iki test/docs
   commit'ini içermiyor; önce kapsam karşılaştırılmalı.
7. #127 ve #155 ikisi de README değiştirir; bu çakışma olasılığıdır, içerik
   eşdeğerliği veya eskimişlik kanıtı değildir. #140 ve #143 farklı smoke
   belgeleri ekler; adları nedeniyle otomatik kapatma adayı sayılmazlar.

### CI ve inceleme kapıları

- [#145 kontrolleri](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/145/checks):
  Windows/Linux Relay ve runtime safety geçti; GitGuardian iki tarihsel
  işaretle başarısız. PR açıklamasındaki sentetik sınıflandırmayı güvenlik
  sahibi doğrulamalı ve mevcut koruma politikasıyla triage etmelidir. Geçmiş
  rewrite veya force push bu planın kapsamında değildir.
- [#156 kontrolleri](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/pull/156/checks):
  aynı GitGuardian durumu ve `control-plane` job'ında AgentBridge
  `STALE_SNAPSHOT`; ilgili düzeltme ayrı incelenip tam yeni HEAD'de tekrar
  test edilmelidir. Başarılı diğer job'lar bu iki hatayı ortadan kaldırmaz.
- #162'de yerel Windows kanıtı: PowerShell 5.1 **54 assertion**, Node
  **50 runner testi**, .NET **29 test**, `git diff --check` geçti. GitHub'da
  yalnız GitGuardian görüldü. Relay workflow'unun PR base filtresi #162,
  #158 ve #160 base'lerini kapsamıyor; gerekli exact-head CI ayrıca
  sağlanmalı, mevcut yeşil sonuç CI kapsamı olarak sunulmamalıdır.
- #132'nin atlanan üç ve #43'ün atlanan bir kontrolü başarılı test değildir.
  PR açıklamalarındaki eski kapsam/test sayıları gerçek diff ve HEAD ile
  doğrulanmalıdır. Gerekli branch protection ve insan review'ü bu envanterle
  kanıtlanmadı.

### Önerilen bağımlılık sıralı plan — işlem yapılmadı

1. Bu snapshot'ı tazeleyin; #162 SHA'sını, mevcut AWS runtime/image digest'ini
   ve tüm dirty worktree'leri dondurulacak referans olarak kaydedin. AWS
   kimliği ve salt okunur gözlem kapsamı doğrulanmadan runtime'a dokunmayın.
2. Sahip, **parça parça zincir** (#94 → #96 → #98 → #118 → #121 → #125 →
   #145) ile **tek konsolidasyon diff'i** (#145 üzerinde tüm ataların
   incelemesi) arasında seçim yapar. Seçim ve insan onayı olmadan PR kapatılmaz.
   Her iki seçenekte eksik #96 `508e04b...` değişikliği, #98 ve sonraki
   dallara güvenli biçimde taşınmalı veya eşdeğerliği kanıtlanmalıdır.
3. #162'yi #145'e karşı dokuz dosyalık ayrı onarım olarak inceleyin; güvenlik,
   Windows/Linux Relay ve .NET exact-head kontrolleri ile izinli review
   tamamlanmadan birleştirmeyin. Korunan #162 dalını rewrite etmeyin.
4. #145'in GitGuardian geçmiş bulgularını sahibinin onaylı triage süreciyle
   çözün. #156'nın CI hatasını ayrıca düzeltip doğrulayın. #145 onarımı
   kabul edildiğinde #156'yı seçilen güncel Relay temeline **non-destructive**
   şekilde entegre edin; onun `develop` base'ini zincire retarget etme veya
   alternatif temiz konsolidasyon seçeneği insan kararına bağlıdır.
5. #156 → #158 → #160 sırasını koruyarak kapsam/CI inceleyin; #160 için
   49 commit, migration, API güvenliği, actor ownership, idempotency,
   Postgres testleri ve varsayılan kapalı davranışı ayrı doğrulayın.
   #158 ve #160 için eksik exact-head workflow'ları çalıştırın. UI kapsamı
   tamamlanmadan tamamlanmış kabul etmeyin.
6. #154'ü `main` ↔ `develop` tabanı ve bağımsız 52 commit açısından ayrıca
   inceleyin; canlı AWS checkout/revizyonu bilinmeden rebase/retarget yok.
   #132 → #138 ayrı authorization incelemesi gerektirir; Agent veya kuyruk
   başlatılması bu denetimin ve no-agent smoke'un dışında kalır.
7. İkinci derecedeki zincirleri kendi sahiplerine ayırın. #127/#155 ve
   #140/#143 için gerçek diff ve korunacak benzersiz bilgiyi karşılaştırın;
   yalnız içerik eşdeğerliği, korunma ve sahip onayı kanıtlanırsa
   “superseded” kararı önerilebilir. Şu an kesin eski/çöp PR yoktur.
8. Her gerçek merge, retarget, rebase, kapatma veya deploy için ilgili insan
   kararını ve gerekiyorsa ayrı onayı alın; sonrasında bağımlı PR base ve
   exact-head CI sonuçlarını yeniden kontrol edin. Bu belge bunları yapmaz.

**Parça parça seçeneğin somut PR sırası:** #94 `develop` incelemesi;
ardından #96'yı `develop` için, eksik `508e04b...` #98 kapsamına güvenle
taşındıktan sonra #98'i `develop` için değerlendirin. Aynı parent-kabul,
base'i `develop`a retarget ve diff/CI yeniden doğrulama döngüsünü sırayla
#118, #121 ve #125 için uygulayın. #162'yi #145 kaynak dalına karşı inceleyip
insan onayıyla kabul ettikten sonra #145'i `develop` için inceleyin. #145
kabul edilirse #156'nın güncel temele non-destructive entegrasyonunu
değerlendirin; sonra #156 → #158 → #160 için aynı parent-kabul/retarget
işlemini uygulayın. Her retarget sonrası gerçek diff ve testler yeniden
incelenir; squash veya farklı merge yönteminde ancestry'nin korunacağı
varsayılmaz. Bunlar uygulanmış işlem veya zorunlu mimari karar değildir.

### Relay → AWS no-agent smoke'a ulaşmak için

1. #161 kapsamında gerçek Watch/inbox'ı ve read-only kimliği belirleyin;
   #145'teki watcher düzeltmesinin o **gerçek** süreç için yeterli olduğunu
   varsaymayın. Offline auth, 403/429/backoff, tek uçuş ve secret-redaction
   testlerini geçirin. Issue #97 sentetik ACK iş akışıyla karıştırmayın.
2. #156'daki sentetik `observe-only` şemasını ve mevcut read-only staging
   gözlemlerini koruyun. Gerçek AWS transport, endpoint/identity, en az yetki,
   request/response korelasyonu, expiry/replay ve doğrulanabilir provenance
   henüz ayrıca tasarım ve uygulama gerektirir; mevcut “observed” güvenilir
   completion veya runtime isolation kanıtı değildir.
3. #160'ın aktör/ownership sınırını, default-OFF handoff route'unu,
   transaction/idempotency ve drift/negatif testlerini bağımsız gözden geçirin;
   mevcut local operator session tek başına task ownership kanıtı değildir.
   İzole PostgreSQL ve Worker kapalı testleri gereklidir. UI uçtan uca hedefi
   seçilirse #158 ve UI preview/status/uncertain-write kapıları da tamamlanır.
4. Operatör ayrı açık izin verirse, **yalnız izole** Windows → AWS → Windows
   sentetik gözlem deneyini belirlenmiş immutable revizyonlarla çalıştırın:
   korelasyon/replay/expiry, kimliksiz erişim, kapalı özellik, duplicate,
   ağ kesintisi ve mevcut dört gözlem regresyonu doğrulansın. Worker/agent
   kapalı, queue değişmez, `executionAllowed=false`; `/tasks/{id}/approve`
   çağrılmaz. Herhangi bir deploy veya canlı Watch başlangıcı ayrıca onay
   gerektirir. Başarısız/eksik kanıt `BLOCKED` kalır.

### Önerilen ortam manifesti — uygulanmış konfigürasyon değil

| Alan | Kaydedilen revizyon / durum | Kanıt ve sınır |
|---|---|---|
| Windows ana repo | `b95bec235a1634bfed0c38079b0fff52ce32007b` | Yerel HEAD, dirty/untracked dosyalar korunur. |
| Windows onarım | `6a7e50ee928c96f28d407e7530d644b3b33e06a9` | #162, ayrı temiz worktree; **koru**. |
| Windows Relay source | `abfefdaa31065c34a2cd15058f39cc07207f22f3` | `%LOCALAPPDATA%\\HICDevRelay\\source` HEAD; çalışan süreç SHA'sı kanıtı değildir. |
| GitHub #154 sandbox | `631a7c09df1378987d6fa6001847dec029129fce` | Remote HEAD; AWS deploy/checkout SHA'sı değildir. |
| AWS Control Plane çalışan commit | **BİLİNMİYOR — KORUNAN** | Bu denetimde canlı AWS sorgusu yapılmadı. Yetkili, salt okunur envanterden tam SHA doğrulanmalı; yeniden başlatma/deploy yapılmamalı. |
| AWS Control Plane image tag ve digest | **BİLİNMİYOR — KORUNAN** | Çalışan container metadata'sı incelenmedi; tag, digest veya host tahmin edilmez. |
| Diğer çalışan container revizyonları | **BİLİNMİYOR — KORUNAN** | Container listesi/metadata sorgulanmadı. |
| #154 sentetik probe image | `sha256:4076016aea5420e5c87eb52280edfc527ace1ce31dc1c9288b1cc4ef9a0e8905` | PR yorumunda operatörce bildirilen tarihsel Python image ID; çalışan Control Plane imajı veya bağımsız provenance kanıtı değil. |
| Önceki staging gözlemleri | 4 adet `observed-untrusted` | [#159](https://github.com/Cabatoy/AI-Agent-Lab-Project-Initialization-Architecture-Blueprint/issues/159) operatör raporu; anlık runtime envanteri değil. |

Manifest gelecekte `observedAt`, ortam/kimlik kapsamı, tam Git commit, image
immutable digest ve kanıt kaynağıyla doldurulmalıdır; kimlik bilgisi,
credential veya sır değeri içermez. Mevcut deployed kaynakların durumunu
değiştirmez.
