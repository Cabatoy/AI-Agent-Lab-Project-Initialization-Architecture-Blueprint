# Dokümantasyon Güncelleme Politikası

## Amaç

Proje ilerledikçe oluşturulmuş doküman setinin güncel kalmasını sağlamak.

## Kural (zorunlu)

Kod, mimari, süreç, operasyon, maliyet veya karar kayıtlarını etkileyen **her anlamlı değişiklikten sonra** kullanıcıya şu soru mutlaka sorulur:

> "Bu değişiklik, oluşturulan/güncel tutulan doküman setine eklensin mi?"

Kullanıcı onayı olmadan doküman setine ekleme yapılmaz.

## Doküman seti kapsamı

- Kaynak Markdown dosyaları: `docs-md/**`
- Yayın/çıktı PDF dosyaları: `docs/**`

## Uygulama adımları

1. Değişiklikten etkilenen doküman başlıklarını belirle.
2. Kullanıcıdan ekleme/güncelleme onayı iste.
3. Onay varsa önce `docs-md` altındaki ilgili TR/EN `.md` dosyalarını güncelle.
4. Sonra eşleşen `docs` altı `.pdf` çıktıları yeniden üret.
5. Güncellenen dosya listesini kullanıcıya raporla.

## Not

Bu politika, dokümantasyonun proje gerçekliğiyle senkron kalması için kalıcı bir çalışma kuralıdır.
