# Yeni Checkout ve Local İnceleme

Bu rehber yeni indirilen checkout'ta dosya incelemesinden Copilot handoff'una, oradan da kullanıcı onaylı salt okunur Podman durum kontrolüne kadar olan sırayı tanımlar. `run.me` yalnızca talimat gösterir; komut, API çağrısı veya Podman işlemi çalıştırmaz. Chat'e gönderilecek metinler `tools/run-me/prompts.tr.json` dosyasında tutulur.

## IDE'de Açma

- **VS Code:** Depo kökünü **File > Open Folder** ile açın. `.github/copilot-instructions.md` repo talimatı olarak yüklenir; yeni sohbette ayrıca `docs/continuation.md` dosyasını okutun.
- **Visual Studio:** Repo kökündeki `AI-Agent-Lab.sln` dosyasını açın. Solution Control Plane API, Agent Worker ve `run.me` projelerini içerir. Copilot talimatlarının otomatik yüklenmesine güvenmeyin; Chat'ten `.github/copilot-instructions.md` ve `docs/continuation.md` dosyalarını açıkça okumasını isteyin.

## Önkoşul ve Başlatma

Rehberi çalıştırmak için .NET 8 SDK gerekir. Podman bu ilk iki adım için gerekli değildir.

Windows PowerShell, depo kökünden:

```powershell
./run.me.ps1
```

macOS veya Linux, depo kökünden:

```bash
bash ./run.me
```

## Sıralı Akış

1. **Dosyaları denetle:** Rehberde `repo review` komutunu çalıştırıp Chat mesajını kopyalayın. Bu adımda Podman, build, ağ isteği, kurulum veya dosya değişikliği yapılmaz. Copilot sonucu bildirdiğinde `done` yazın.
2. **Sohbeti kaldığı yerden sürdür:** Rehberde `handoff check` komutunu çalıştırıp Chat mesajını gönderin. Handoff öneridir; onaylanmış karar değildir. Dosya değişikliği veya Podman işlemi yapılmaz. Sonucu gördükten sonra `done` yazın.
3. **Podman incelemesine izin ver:** Rehber `approve-podman` yazmanızı ister. Bu onay yalnızca durum komutlarının gösterilmesine/çalıştırılmasına izin verir; machine başlatma, ayar değiştirme, build, container başlatma, volume silme veya prune izni değildir. İzin vermiyorsanız `quit` veya `Ctrl+C` ile çıkın.
4. **Salt okunur durumu kontrol et:** Rehberde `podman check` komutunu çalıştırıp Chat mesajını gönderin. Copilot yalnızca `podman --version`, `podman machine list`, `podman compose version` ve `podman compose ps` komutlarını çalıştırmalıdır. Machine durmuşsa başlatmayın; sonucu raporlayıp durun. `scripts/local.ps1 status` machine başlatabileceği için bu kontrolde kullanmayın.
5. **Sonucu değerlendir ve karar ver:** Sorun yoksa akışı `done` ile tamamlayın. Sorun varsa run.me konsolunda `approve-setup` yazarak yalnızca gerekli setup/onarım komutlarına ayrı açık onay verin.
6. **Onaylı setup/onarım uygula:** `approve-setup` sonrası rehberde `setup repair` komutunu çalıştırıp Chat mesajını gönderin. Copilot yalnızca repository scriptleriyle işlem yapmalıdır (`./scripts/local.ps1 new-build` veya `./scripts/local.sh new-build`). Windows'ta user-mode networking değişikliği gerekirse etkiyi ayrıca açıklatın ve ayrı açık onay verin; onaysız ağ ayarı değişikliği yaptırmayın.
7. **Sonuçları konuşun:** Setup/onarım sonucunu Copilot'tan özetletin. `rebuild-all` veya `delete-all` gibi veri silen işlemlere geçmeden önce kapsamı ayrıca onaylayın.

## Rehber Komutları

| Komut | İşlev |
|---|---|
| `help` veya `?` | Tüm komutları gösterir |
| `status` | Adım ilerlemesini gösterir |
| `step <n>` | Listedeki istenen adımı doğrudan çağırır (`step 1` ... `step 6`) |
| `repo review` | Proje dosya inceleme Chat mesajını gösterir |
| `handoff check` | Handoff karşılaştırma Chat mesajını gösterir |
| `podman check` | Salt okunur Podman kontrol Chat mesajını gösterir |
| `setup repair` | Setup/onarım Chat mesajını gösterir |
| `done` | Dosya/sohbet adımını tamamlayıp ilerler |
| `approve-podman` | Yalnızca salt okunur Podman durum kontrolüne izin verir |
| `approve-setup` | Sorun varsa setup/onarım komutlarına ayrı açık onay verir |
| `reset` | Onay alarak yalnızca rehber ilerlemesini sıfırlar |
| `quit` veya `exit` | Kaydedilmiş ilerlemeyle çıkar |
| `Ctrl+C` | Güvenli biçimde çıkar; ilerleme korunur |

İlerleme depo kökündeki `.run-me-state.json` dosyasına kaydedilir ve Git tarafından yok sayılır. Başka makineye veya yeni clone'a taşınmaz; sohbet hafızası değildir. `reset`, Podman container'larına, volume'lere veya task kayıtlarına dokunmaz.

## Local Task Smoke Test

Podman stack'ini başlatma ve task API akışını deneme bu ilk inceleme rehberinin dışındadır. Kullanıcı bu adıma ayrıca karar verirse [README'deki local Podman](../README.md#local-podman-kurulumu) ve task API adımlarını izleyin. Worker şu an yalnızca kuyruk/durum smoke test'i yapar; task açıklamasını kullanarak kod üretmez.

## Örnek run.me Oturumu

Aşağıdaki örnek yalnızca rehber komut akışını gösterir:

1. `repo review` → Chat mesajını kopyala ve gönder
2. Copilot yanıtını oku, konsola dönüp `done` yaz
3. `handoff check` → ikinci mesajı gönder
4. Copilot yanıtı sonrası `done`
5. Podman kontrolü istenirse `approve-podman`, ardından `podman check`
6. Sorun yoksa `done`; sorun varsa `approve-setup` ve `setup repair`

Not: `done`, `approve-podman` ve `approve-setup` komutlarını Chat'e değil, `run.me` konsoluna yazın.