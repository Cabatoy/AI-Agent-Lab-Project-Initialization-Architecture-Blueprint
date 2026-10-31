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
- `run.me` ve `run.me.ps1` önce dosya denetimini, sonra handoff'tan sohbeti sürdürmeyi, son olarak açık onayla salt okunur Podman durum kontrolünü yönlendiriyor. Sorun çıkarsa setup/onarım komutları için ayrı kullanıcı onayı ister. Hiçbir komutu kendileri çalıştırmıyor.
- `run.me` ilerlemesi `.run-me-state.json` dosyasında yalnızca o checkout'ta tutulur ve Git tarafından yok sayılır. Başka makineye veya yeni clone'a taşınmaz; bu dosya sohbet bağlamı değildir.
- Worker şu an yalnızca kuyruk ve task durum geçişi smoke test'i yapar; task açıklamasını kullanarak kod üretmez veya gerçek ajan işi yürütmez.
- Güncel console akışı .NET 8 ile izole output klasörüne derlendi; geçici checkout'ta `approve-podman` kapısı ve start komutu üretmeme davranışı smoke test edildi. Setup/onarım için ayrı onay kapısı eklendi. macOS runtime doğrulaması henüz yapılmadı.

## Açık Teknik Takip

Önceki local bootstrap incelemesinde aşağıdaki noktalar çözülmeden kaldı; kod değişikliği yapılmadı:

- `scripts/local.sh` boş `cgroup.subtree_control` içeriğinde `pids` etkinleştirmeyi atlayabilir; ayrıca cgroup kontrolü `status` ve `delete-all` sırasında da çalışır.
- Compose, Redis için yalnızca `service_started` bekliyor; Control Plane ve Worker başlangıçta Redis bağlantısı kuruyor. Başlangıç readiness davranışı doğrulanmalı.
- `rebuild-all` ve `delete-all` `--rmi all` kullanıyor; `postgres` ve `redis` gibi paylaşılan upstream image'ların temizleme kapsamı doğrulanmalı.
- Host portları sabit (`8080`, `5432`, `6379`); port çakışması halinde alternatif port keşfi yok.
- `run.me` akışının temiz macOS checkout'unda ve yeni kullanıcı ortamında denenmesi gerekiyor.

## Önerilen Devam Sırası

Yeni checkout'ta önce dosya denetimini ve handoff karşılaştırmasını tamamlayın. Kullanıcı açıkça izin verirse yalnızca salt okunur Podman durum kontrollerini yapın; machine durmuşsa başlatmadan raporlayıp durun. Sorun varsa setup/onarım için ayrı açık onay alıp yalnızca repository lifecycle scriptleriyle ilerleyin. Sonraki kapsamı sonuçları kullanıcıyla konuştuktan sonra belirleyin. Bu bir öneridir; onaylanmış karar değildir.