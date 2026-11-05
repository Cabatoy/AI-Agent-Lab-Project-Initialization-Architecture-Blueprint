# ADR-001: Local prototipte Podman kullanımı

- **Tarih:** 2026-10-31
- **Durum:** Onaylandı
- **Sahip:** Proje sahibi

## Bağlam

İlk Agent Lab dikey dilimini cloud provisioning öncesinde Windows/macOS geliştirici ortamında tekrarlanabilir biçimde çalıştırıp doğrulamak gerekiyor. Prototip API, worker, PostgreSQL ve Redis servislerinden oluşuyor. Windows WSL Podman machine'de container başlatmak için `pids` cgroup delegasyonu gerekebiliyor; host `localhost` port yönlendirmesi her kurulumda çalışmayabilir.

## Karar

- Local prototip container runtime'ı Podman Desktop ve Podman Compose'tur.
- Repo, Windows için `scripts/local.ps1`, macOS/Linux için `scripts/local.sh` lifecycle scriptleri sağlar.
- `new-build` mevcut task verisini korur; `rebuild-all` ve `delete-all` PostgreSQL/Redis volume'lerini siler ve öncesinde açık onay ister.
- Windows/WSL scripti user-mode networking'i yalnızca `-AllowWslNetworkingChange` ile açık onaydan sonra etkinleştirir, eksik `pids` cgroup delegasyonunu machine çalışırken uygular ve erişilebilir API adresini keşfeder. Cgroup ayarı her machine başlangıcında tekrar kontrol edilir.
- Podman AI Lab yerel model deneyi için opsiyonel araçtır; Worker entegrasyonu, cloud runtime'ı veya production model sağlayıcısı olarak seçilmemiştir.
- Bu karar AWS veya production container runtime tercihi değildir. AWS provisioning yapılmaz.

## Değerlendirilen alternatifler

- **Docker Desktop Compose:** İşlevsel alternatif; local prototip için Podman tercihi nedeniyle ana kullanım yolu yapılmadı.
- **Doğrudan host servisleri:** Container ortamları arasındaki farkı azaltmadığı ve onboarding'i değiştirdiği için seçilmedi.
- **Podman cgroups'i tamamen kapatmak:** Resource controller sınırlarını kaldırabileceği için seçilmedi.

## Sonuçlar

- Yeni katkıcı Podman Desktop'ı kurduktan sonra repo Chat promptları veya scriptleriyle local stack'i hazırlayabilir.
- `run.me` ve `run.me.ps1`, yeni checkout'ta dosya denetimi ve handoff'tan sonra açık onaylı salt okunur Podman durum kontrolünü yönlendirir; lifecycle komutlarını çalıştırma sorumluluğu mevcut scriptlerde kalır.
- Windows/WSL'de API adresi `localhost` değil Podman machine IP'si olabilir; user-mode networking host erişimini sağlar ve script adresi keşfedip yazdırır. User-mode networking Podman machine çalışırken diğer WSL dağıtımlarının ağını da etkileyebilir; bu nedenle otomatik ve onaysız etkinleştirilmez.
- Cgroup workaround yalnızca gerektiğinde uygulanır; WSL dışındaki sistemlerde ilgili cgroup yolu bulunmazsa atlanır.
- Local model çalıştırmak donanım belleği/CPU/GPU ve disk kullanır; AI Lab/model kurulumu base stack'in önkoşulu değildir.
- Yeniden kurulum ve silme işlemlerinin kapsamı yalnızca repo Compose projesidir; global Podman prune kullanılmaz.