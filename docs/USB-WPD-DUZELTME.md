# USB WPD okuma hatası — 6 Kasım 2026

## Doğrulanan neden

Gerçek iPhone ile uygulama kapalıyken de tekrarlandı: bir WPD kaynağı yarıda kapatıldıktan sonra sonraki dosya isteği önceki dosyanın baytlarını döndürebiliyor. İncelenen iki ardışık JPG'de önceki dosyanın SHA-256 değeri ve uzunluğu döndü; beklenen uzunluk kontrolü yayımı engelledi. GetStream hataları `0x8007001e` / `0x80042007` ve erken EOF aynı incelemede görüldü.

Yalnızca Cancel/Disconnect/Connect kullanmak yeterli olmadı. Bozuk kayıtlı bölümle karşılaşınca hemen hata atmak, sonraki iPhone okumasını yeniden bozabiliyordu.

## Düzeltme

- Küçük fotoğraf ve küçük resim okumaları sınırları içinde bitirilerek kapatılır. Önizleme için büyük video bütünüyle okunmaz; mevcut 12 MiB fotoğraf / 4 MiB küçük resim sınırları korunur.
- Hatalı/yarım kaynak oturumu geçersiz kılar. Sonraki açılışta bağlantı yeniden kurulur; oturuma bağlı WPD nesne kimlikleri kaynak konumundan yeniden çözümlenir, boyut/değişim bilgisi tekrar kontrol edilir.
- Bozuk kayıtlı bölüm sıfırlanır; başlatılmış aktarımın kaynak okuması tamamlandıktan sonra işlem reddedilir. Bu denemede dosya yayımlanmaz ve geçmişe yazılmaz. Sınırlı yeniden deneme temiz kopyayı oluşturur.
- WPD okuma hataları bağlantı hatası olarak sınıflanır; yeniden denemeler tükenirse kuyruk bekletilir. Yerel diske ait diğer hataların sınıflandırması korunur.
- Kullanıcı durdurduğunda mevcut kaynak akışı açık kalır; devam ettirmek yeni bir WPD okuması başlatmaz. Seçili kopyalama ve yeni dosya yedekleme aynı çekirdeği kullanır.
- SHA-256, beklenen uzunluk, güvenli hedef yolu ve mevcut dosyanın üzerine yazmama davranışları korunur.

## Çalıştırılan testler

- `dotnet run --project tests/Aktarim.Usb.Tests/Aktarim.Usb.Tests.csproj -c Release`: kaynak ömrü, sınırlı okuma, hata sınıflandırması, erken EOF, bozuk bölümün EOF öncesi reddedilmemesi, sonraki temiz kopyanın SHA-256 doğrulaması ve sonraki parçada uyuşmazlık regresyonları.
- `-- --iphone-read-only`: bağlı gerçek iPhone'da 3.894 medya tarandı. Üç JPG, bir HEIC ve bir MOV bağımsız tam okuma ile örneklendi. Dört küçük kaynağı yarıda kapatma sonrası beş dosyanın her biri tekrar okundu: 20 SHA-256 karşılaştırması geçti. Beş dosyanın her birinde bozuk yerel bölüm reddedildi; durdur/devam sonrası hedef dosya başlangıçtaki SHA-256 ile eşleşti. Büyük MOV erken kapatılıp oturum yeniden açılınca da SHA-256 eşleşti. Telefona veya mevcut yedeklere yazılmadı; yerel denemeler ayrı geçici klasörde yapıldı.
- WPF sahte USB: yedekleme/geçmiş, etkin önizlemeyi iptal etme, video kuyruğu, art arda kısa okuma sonrası kurtarma; TR/EN seçili kopyalama ve yedekleme için durdur/devam, durdururken seçim ekleme, uygulama kesintisinden devam ve tamamlananları koruma.
- Durdurma UI testinde küçük test videosuyla bir zamanlama hatası görüldü; test penceresi büyütüldü ve ara ilerleme bekleyişi ondalık değerlerle yanlış eşleşmeyecek şekilde düzeltildi. Dört TR/EN durdurma senaryosu yeniden çalıştırıldı ve geçti.
- Windows CI kaynak ömrü testini de çalıştırır. Yalıtılmış runner'daki çekirdek, hedef yolu/junction yarışları, LAN, WPF, Safari ve taşınabilir EXE sonuçları taslak PR'daki CI çalışmasında izlenir.

## Sınırlar / bekleyen doğrulamalar

- Bu düzeltmeyle fiziksel kablo çekme ve 5 GB üzeri aktarım yeniden çalıştırılmadı. Önceki sürümde kullanıcının başarılı 5 GB üzeri testi bu sürüm için yeni bir test sayılmaz.
- WPD kaynak SHA-256 sağlamıyor. Yerel SHA-256, alınan baytların diske doğru yazıldığını kanıtlar; sürücünün aynı uzunlukta yanlış kaynak verisi döndürmesini tek başına dışlamaz. Yukarıdaki gerçek cihaz testinde bağımsız tam okumalar karşılaştırıldı.
- Zorla kapatma / gerçek bağlantı kaybı sürücü durumuna bağlı kalabilir. Yeniden denemeler tükenirse kilidi açık cihazı yeniden bağlayıp taramak gerekebilir; yarım dosya tamamlandı sayılmaz.

## Manuel cihaz tekrarı

1. Önceki EXE'yi kapatın; iPhone kilidi açık ve güven onayı verilmiş olsun. Yeni EXE'de boş bir test hedefi seçin.
2. JPG, HEIC ve MOV seçin; **Seçilenleri kopyala** ile başlatın. Durdurup bekleyin, bir dosya daha seçin, devam ettirin. Tamamlanan dosyaların tek kopya olduğuna bakın.
3. Aynı seçimle tekrar başlatın; doğrulanmış kopyalar atlanmalı. Bir hedef kopyayı ayrı yere taşıyın; yeniden aktarım eksik dosyayı oluşturmalı, mevcut diğer dosyaları değiştirmemeli.
4. Büyük MOV aktarılırken kabloyu çıkarın. Kuyruk anlaşılır bağlantı hatasıyla beklemeli; yarım MOV tamamlandı görünmemeli. Kilit açıkken yeniden bağlayın, cihazı bulun/tarayın ve tekrar başlatın. Kayıtlı bölüm önce kaynakla doğrulanmalı.
5. `Get-FileHash -Algorithm SHA256 <dosya>` ile tamamlanmış bağımsız iki kopyayı karşılaştırın. Hata olursa tanılama satırını ve işlemin hangi adımda olduğunu kaydedin.
