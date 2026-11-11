# Offline execution provider deneyi

Bu bağımsız .NET 10 bileşeni #106 için **yalnızca bellek içi simülasyondur**.
Worker, Control Plane, veri tabanı, model, SDK, ağ ve işletim sistemi sandbox'ına
bağlı değildir; hiçbir işlem süreç, container veya bulut kaynağı oluşturmaz.
`Started` yalnız fake provider kaydıdır; HIC task durumu veya çalışma kanıtı değildir.
Tüm gözlemler `OFFLINE_TESTED; OS_UNVERIFIED` olarak işaretlenir.

## Teknik sınırlar

- Manifest immutable scalar alanlardan oluşur. `Digest`, .NET 10 System.Text.Json
  varsayılan özellik sırası ve UTC zamanıyla hesaplanan SHA-256'dır; iç prototip
  biçimidir, bağımsız diller arası imza standardı veya gerçek imzalı manifest değildir.
- Yetki, consent ve proof `ISyntheticTrustSource` üzerinden ayrı enjekte edilir.
  Bu interface bir doğrulayıcı, anahtar kaydı veya güvenilir ledger implementasyonu
  **değildir**; test fixture'ındaki boolean'lar gerçek imza veya izolasyon kanıtı değildir.
- Seçim ve işlem öncesi admission aynı kontrolleri kullanır; consent tam provider
  instance, environment, bölge, gizlilik ve maliyet kapsamına bağlıdır.
  Değişen seçim yeni consent gerektirir. Fake proof yetki, challenge, manifest ve
  capability özetine bağlıdır; gerçek OS kanıtı olarak kabul edilemez.
- Kilit ve idempotency yalnızca aynı provider nesnesinin belleğinde geçerlidir.
  Restart, çoklu process/provider arasında durable claim veya replay koruması,
  gerçek cancellation ve cleanup doğrulaması sağlanmaz. Fake handle yetki değildir.
- Kaynak staging, dosya/çıktı doğrulama, log sanitization, gerçek kota/harcama
  kontrolü ve bağımsız kanıt burada uygulanmaz. #67, #102/#103 ve #108 ile
  ilgili gerçek güvenlik kapıları ve ayrıca kullanıcı onayı hâlâ gereklidir.

## Doğrulama

`dotnet build tests/Hic.ExecutionProviders.Tests/Hic.ExecutionProviders.Tests.csproj -c Release`
ve `dotnet test tests/Hic.ExecutionProviders.Tests/Hic.ExecutionProviders.Tests.csproj -c Release --no-build --no-restore`.
Bu testler gerçek izolasyon, gizli veri ayrımı veya canlı çalıştırma kanıtı değildir.
