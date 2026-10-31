# Devam Noktası

Bu belge yeni bir checkout veya Copilot sohbeti için kısa ve kalıcı handoff kaynağıdır. Sohbet transcript'i değildir; doğrulanmış proje durumunu ve henüz çözülmemiş işleri taşır. Öneriler onaylanmış mimari karar değildir.

## Depoyu İndirme

1. Depoyu klonlayın. VS Code'da repo kökünü **File > Open Folder** ile açın; Visual Studio'da repo kökündeki `AI-Agent-Lab.sln` dosyasını açın.
2. `AI-Agent-Lab.sln`, Control Plane API, Agent Worker ve `run.me` console projelerini içerir. Solution Explorer'dan çalışacağınız projeyi seçin; `dotnet build AI-Agent-Lab.sln` tüm .NET projelerini derler.
3. Önce `.github/copilot-instructions.md`, bu handoff dosyası, `README.md`, karar/açık-soru/yol-haritası belgeleri ve kaynak proje yapısını salt okunur kontrol edin. `bin/` ve `obj/` çıktıları kaynak incelemesine dahil değildir.
4. Copilot talimatı VS Code'da repo ayarı olarak yüklenir. Visual Studio'da otomatik yükleme sürüme göre değişebileceği için Copilot Chat'ten talimat dosyasını açıkça okumasını isteyin.
5. Podman Desktop/Compose veya .NET 8 SDK kurulumu gerekiyorsa önce gereksinimleri konuşun. Podman kontrolü ve başlatma adımları birbirinden ayrıdır; [local onboarding](local-onboarding.md) içindeki onay kapılarını izleyin.

## Yeni Sohbetten Devam

Copilot Chat'e şu isteği gönderin:

> Bu repo için önce dosyaları salt okunur kontrol et; `.github/copilot-instructions.md` ve `docs/continuation.md` dosyalarını oku. Handoff'taki durumu mevcut repo ile karşılaştır, doğrulanmış gerçekleri ve önerileri ayır, sıradaki işi kısaca bildir. Kullanıcı ayrıca onay vermeden Podman komutu, build, ağ isteği veya dosya değişikliği yapma.

Sohbet geçmişi Git'e veya `run.me` durum dosyasına yazılmaz. Büyük bir işi bitirdikten sonra bu belge güncellenirse sonraki sohbet, eski konuşmayı aramadan projedeki devam noktasını bulabilir.

## Doğrulanmış Durum

- Repo, mimari belgeler ve Podman Compose ile çalışan local prototip içeriyor; AWS provisioning veya production deployment yapılmadı.
- Repo kökündeki `AI-Agent-Lab.sln` üç .NET projesini Visual Studio solution'ında bir araya getiriyor.
- Local stack Control Plane API, Agent Worker, PostgreSQL ve Redis servislerinden oluşuyor.
- `run.me` ve `run.me.ps1` önce dosya denetimini, sonra handoff'tan sohbeti sürdürmeyi yönlendiriyor; bu iki adımda Chat mesajı göstermekten öte bir şey yapmıyor. `approve-podman` onayından sonra salt okunur Podman durum kontrolünü (`podman --version`, `podman machine list`, `podman compose version`, `podman compose ps`) kendisi çalıştırıyor; sorun bulunursa ve kullanıcı `approve-setup` ile açıkça onaylarsa `scripts/local.ps1`/`scripts/local.sh new-build` komutunu kendisi başlatıyor. Chat mesajlarını `tools/run-me/prompts.tr.json` dosyasında `repo review`, `handoff check`, `podman check`, `setup repair` komutlarıyla veya doğrudan `step <n>` ile de gösterebilir.
- `run.me` ilerlemesi `.run-me-state.json` dosyasında yalnızca o checkout'ta tutulur ve Git tarafından yok sayılır. Başka makineye veya yeni clone'a taşınmaz; bu dosya sohbet bağlamı değildir.
- Worker task detayını Control Plane'den alır, prompt üretir ve opsiyonel model provider üzerinden çağrı yapar. Podman AI Lab için önce `chat/completions`, sonra `completions` fallback'i dener; geçici hatalarda retry uygular.
- `docker-compose.yml` içinde Worker için `MODEL_PROVIDER`, `MODEL_ENDPOINT`, `MODEL_NAME`, `MODEL_API_KEY`, `MODEL_TIMEOUT_SECONDS` ortam değişkenleri bulunur.
- `scripts/local.ps1` ve `scripts/local.sh` `bootstrap-dev` komutunu destekler. Bu komut stack kurulumunu, AI Lab alias bağlamasını (`ai-lab`), `.env` üretimini ve smoke test'i otomatikler. `-Reset`/`--reset` ile veri silerek temiz başlangıç yapılabilir.
- `bootstrap-dev -Reset` uçtan uca çalıştırılarak sıfırdan local kurulum ve smoke test (`task -> approve -> completed`) doğrulandı.

## Önerilen Devam Sırası

Yeni checkout'ta önce dosya denetimini ve handoff karşılaştırmasını tamamlayın. Kullanıcı açıkça izin verirse yalnızca salt okunur Podman durum kontrollerini yapın; machine durmuşsa başlatmadan raporlayıp durun. Sorun varsa setup/onarım için ayrı açık onay alıp yalnızca repository lifecycle scriptleriyle ilerleyin. Ardından `bootstrap-dev` komutuyla `.env` ve smoke test otomasyonunu çalıştırın. Bu bir öneridir; onaylanmış karar değildir.