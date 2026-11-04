# Fiziksel iPhone test planı — bekliyor

USB akışı sahte kaynakla sınanmıştır; fiziksel iPhone kopyalama sonucu bekliyor. Wi-Fi aktarımı Windows'ta Chrome ile uçtan uca sınanmıştır; iPhone Safari ve 5 GB üzeri sonuçları bekliyor. Her adım için Windows sürümü, iPhone modeli, iOS sürümü, kablo, ağ, dosya boyutu, sonuç ve varsa hata kaydedilir.

## USB-C

1. Windows 11 bilgisayarda uygulamayı aç, hedef olarak yeterli boş alanı olan NTFS klasörü seç. Aynı adlı bir hedef dosyanın içeriğini ayrıca sakla.
2. iPhone'u veri destekli USB-C kablosuyla bağla; kilidini aç ve istenirse bilgisayara güven. USB ekranında **Aygıtları bul** düğmesine bas, cihazı seç ve **Medyayı tara** düğmesine bas. Görünür medya ve Dosyalar'daki içerik durumunu kaydet.
3. Hedef klasörü seç, bir HEIC ve bir MOV işaretleyip **Seçileni kopyala** düğmesine bas. Kaynakta sunulan ad ve boyut ile hedef dosyayı karşılaştır. Yerel kopya doğrulamasının bağımsız kaynak hash'i olarak sunulmadığını kontrol et.
4. 5 GB üzeri bir videonun aktarımı sürerken kabloyu çıkar. Yarım dosyanın tamamlandı görünmediğini ve mevcut hedef dosyasının değişmediğini kontrol et. Yeniden bağla, dosyayı yeniden seç ve baştan tamamlat.
5. Uygulamayı aktarım sırasında kapatıp açarak, ayrıca yeterli alanı olmayan bir hedef seçerek tekrarla. Tamamlanmış dosyaların korunduğunu ve hata mesajlarının anlaşılır olduğunu kontrol et.
6. iPhone'un Fotoğraflar aktarım ayarı ve elde edilen HEIC/MOV biçimini kaydet. AirDrop ile Fotoğraflar'a ve Dosyalar'a kaydedilen örnekleri ayrı ayrı dene.

## USB önizleme — gerçek cihaz sonuçları

Windows iPhone'u algıladı; ilk otomatik medya taraması cihaz işlemi hatası verdi. Kilit ve güven onayı sonrasındaki otomatik yeniden tarama iki dakikadan uzun sürede tamamlanmadığı için durduruldu. Kullanıcı daha sonra gerçek iPhone'da üç biçimin de önizlemesinin göründüğünü bildirdi:

| Biçim | Gerçek iPhone önizlemesi |
| --- | --- |
| JPG | Görünüyor — kullanıcı bildirimi |
| HEIC | Görünüyor — kullanıcı bildirimi; Windows'ta HEIC çözücüsü sonucu etkileyebilir. |
| MOV | Görünüyor — kullanıcı bildirimi; yalnızca aygıt küçük resmi kullanılır. |

USB ekranında aygıtı seçip medyayı tara; aramaya sırasıyla `.jpg`, `.heic` ve `.mov` yaz. Her biçimde görünür dosyalarda gerçek küçük resim veya biçim rozeti göründüğünü ayrı kaydet; aşağı kaydırıp başka öğelerin yüklendiğini, ardından seçili dosya kopyalamasının tamamlandığını kontrol et. Her sonuçla cihaz/iOS/Windows sürümünü ve HEIC çözücüsünün kurulu olup olmadığını kaydet. Otomasyon için Release derlemesinden sonra `powershell -ExecutionPolicy Bypass -File tests/iphone-previews.ps1` çalıştırılabilir; dosya adlarını raporlamaz.

## Safari ve Wi-Fi

1. Bilgisayar ve iPhone'u aynı özel ağa bağla. Uygulamanın Wi-Fi ekranında bu ağı taşıyan arayüzü seç. HTTPS için iPhone'un güvendiği ve seçilen IPv4 adresini SAN alanında içeren, özel anahtarlı PFX edin; gerekirse yetkili kök sertifikayı iPhone'a güvenilir olarak kur ve tam güveni etkinleştir. PFX'i ve parolasını seç. Alternatif olarak güvenilen bir ağda **Şifresiz yerel aktarım (HTTP)** seç. Hedef klasörü seç ve alıcıyı başlat. Güvenlik duvarı sorarsa erişimi yalnızca özel ağ için aç; bağlantı kurulamazsa seçilen arayüzün ve Windows güvenlik duvarının uygulamaya izin verdiğini kontrol et. Sertifika uyarısı görürsen güven zincirini ve IP eşleşmesini düzelt.
2. iPhone kamerasıyla QR kodunu okut ve Safari'de aç. Windows'ta isteği önce reddet. Alıcıyı kapatıp yeniden başlatarak yeni QR üret; yeniden okut ve isteği Windows'ta kabul et. Eski QR'ın tekrar kullanılamadığını kontrol et. QR beş dakika içinde kullanılmalıdır.
3. Dosyalar seçicisinde birden fazla dosya seç ve gönder. Dosya seçicinin kartın içinde kaldığını, her dosyanın ayrı tamamlandığını kontrol et. Gönderenin SHA-256 değeriyle Windows hedefindeki `Get-FileHash -Algorithm SHA256 <dosya>` sonucunu karşılaştır.
4. Birden çok parçalı dosyayı gönderirken en az bir parça gönderildikten sonra Wi-Fi bağlantısını kes. Hedefte tamamlanmış dosya bulunmadığını kontrol et. Ağa dön; açık sayfanın otomatik devam ettiğini gözlemle. Ayrı bir denemede kesinti sırasında sekmeyi kapat, ağ dönünce sayfayı yeniden aç ve aynı dosyayı seç: doğrulanmış parçalar yeniden kullanılmalı. İşlem bitince kaynak ve hedef SHA-256 değerlerini karşılaştır. Bunu ayrıca 5 GB üzeri dosyayla ve iki bağlantı modunda tekrarla. Sonucu dosya boyutu ve geçen süreyle kaydet.
5. Ayrı denemelerde Safari sekmesini kapat, ekranı kilitle ve Windows'u uyutup uyandır. Her birinde tamamlanma ve yeniden seçim davranışını kaydet; kesintisiz arka plan desteği varsayma.
6. Geçersiz sertifika ve farklı ağdan erişim denemelerini yap. Reddedilmeleri gerekir.

## Windows paylaşımı

1. Özel ağda, ayrı ve yalnızca yetkili kullanıcıya açık bir Windows klasör paylaşımı oluştur; SMB1 ve misafir erişimini açma.
2. iPhone'da Dosyalar → Göz At → … → Sunucuya Bağlan bölümünden `smb://PC-IP` adresine bağlan, kullanıcı bilgileriyle oturum aç ve videoyu kopyala.
3. Dosyalar'da tamamlanmayı, Windows'ta boyut ve oynatmayı kontrol et. 5 GB üzeri dosya ve ekran kilidi senaryolarını ayrıca dene. Bu yolun uygulama tarafından otomatik hash doğrulaması veya devam garantisi yoktur.
