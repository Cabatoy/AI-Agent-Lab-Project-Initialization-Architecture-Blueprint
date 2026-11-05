# 00 - Proje Vizyonu

## Vizyon

AI Agent Lab; bir ürün sahibinin doğal dille gereksinim verebildiği, yapay zekâ ajanlarının bu gereksinimleri incelenebilir planlara ve doğrulanabilir değişikliklere dönüştürdüğü, insan onaylarıyla yönetilen genel amaçlı bir yazılım geliştirme platformu olacaktır.

Platform, tek bir müşteri uygulamasına gömülü olmayacaktır. İlk müşteri olan pazaryeri, başka projelerin de daha sonra eklenebileceğini kanıtlayan bağımsız bir projedir; platformun mimarisi pazaryerine özel iş kurallarına bağlanmamalıdır.

## Kullanıcı ve çalışma biçimi

Ürün sahibi; gereksinimleri, öncelikleri ve onayları yönetir, gerektiğinde Pull Request inceler. Günlük kod yazma platformun hedef kullanıcı iş akışının parçası değildir. İki insan geliştirici gerektiğinde katkı verebilir; Windows ve macOS iş akışları desteklenmelidir.

İşlerin çoğunu ajanlar üstlenirken insanlar kapsamı, önemli mimari kararları ve üretim dağıtımını denetler. Ajan çıktısı doğrulanana kadar güvenilmez kabul edilir.

## Hedefler

- Birden fazla projeyi ve proje kapsamlı yapılandırmayı ortak bir Control Plane üzerinden yönetmek.
- Sohbetten görev, gereksinim analizi, teknik plan, onay ve yürütme akışına geçebilmek.
- Her görevi izole, sınırlı ve kurtarılabilir bir bulut çalışma alanında yürütmek.
- Ajan tanımlarını ajan yürütme oturumlarından ayırmak; ortak rol tanımlarını proje verisi veya yetkisiyle karıştırmamak.
- Test, güvenlik kontrolü ve insan incelemesini GitHub Pull Request akışına bağlamak.
- Model sağlayıcılarını değiştirebilecek ve göreve göre yönlendirme yapabilecek bir yapı kurmak.
- Harcamaları, denetim izini, hataları ve insan onaylarını görünür kılmak.

## Kapsam dışı ve sınırlar

- Local Podman prototipi, container yaşam döngüsünü ve ilk task workflow'unu doğrulayan sınırlı bir geliştirme aracıdır; üretim platformu değildir.
- Kullanıcı açıkça onaylamadıkça AWS kaynağı oluşturulmaz/provision edilmez, ücretli servis başlatılmaz veya deploy yapılmaz.
- Platform, kişisel bilgisayar, ev ağı veya evde barındırılan servis olmadan çalışabilmelidir.
- Üretim dağıtımı açık insan onayı gerektirir.
- Pazaryerinin güvenlik, gizlilik, yaş güvencesi, moderasyon ve yasal uyum ihtiyaçları ilk tasarımdan itibaren ele alınır; ürün kapsamı ve ilgili hukuk alanları ayrıca netleştirilir.

## Başarı göstergeleri (öneri)

İlk kilometre taşlarında başarı; tekrarlanabilir bir proje onboarding süreci, kalıcı workflow durumu, görev başına izole yürütme, insan onaylı PR üretimi, Windows/macOS katkı uyumu ve ölçülebilir maliyet/denetim kayıtları ile değerlendirilebilir. Sayısal SLO, bütçe eşikleri ve kabul ölçütleri henüz kararlaştırılmamıştır.