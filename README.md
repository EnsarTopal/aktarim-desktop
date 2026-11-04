# Aktarım

## İndir

[En son sürümden Aktarim.exe dosyasını indirin](https://github.com/EnsarTopal/aktarim-desktop/releases/latest). Uygulama 64 bit Windows içindir; ayrıca .NET kurulumu gerektirmez. EXE dijital olarak imzalanmamıştır ve Windows güvenlik uyarısı gösterebilir.

Windows WPF uygulaması USB üzerinden iPhone medya seçimini ve doğrulanmış yerel kopyalamayı, ayrıca iPhone Safari üzerinden HTTPS ile dosya alımını sunar. Arayüzler Türkçe ve İngilizcedir. Dil ve hedef ayarları yerel uygulama verilerinde saklanır.

Windows'ta .NET SDK 10.0.403 ile çalıştırın:

```powershell
dotnet run --project src/Aktarim.Desktop
```

USB ekranında **Aygıtları bul**, aygıtı seç, **Medyayı tara**, dosyaları işaretle ve hedef klasörü seçtikten sonra **Seçileni kopyala** düğmesine bas. Uygulama WPD'nin sunduğu boyutu ve okunan bayt sayısını karşılaştırır, yazılan dosyayı yeniden okuyarak SHA-256 değerini kontrol eder. Bu bağımsız bir iPhone kaynak hash'i değildir. Tamamlanan dosyalar korunur, kesilen USB dosyası yeniden seçildiğinde baştan başlar. Mevcut dosyalar üzerine yazılmaz.

Wi-Fi ekranında iPhone ile aynı yerel ağ arayüzünü seçin. HTTPS için iPhone'un güvendiği, seçili IPv4 adresini kapsayan özel anahtarlı PFX sertifikasını seçin. Alternatif olarak yalnızca güvendiğiniz ağda **Şifresiz yerel aktarım (HTTP)** seçebilirsiniz: bu modda dosya ve oturum bilgileri ağdaki kişilerce okunabilir veya değiştirilebilir. Alıcıyı başlatın, QR kodunu iPhone kamerasıyla okutun, Safari'de açın ve Windows'ta isteği onaylayın. QR anahtarı beş dakika geçerli ve tek kullanımlıktır. IP ve port otomatik belirlenir; alıcı yalnızca seçilen arayüzü dinler ve kapatılınca durur. Dosyalar'dan birden fazla dosya seçilebilir ve bunlar sırayla aktarılır. Bağlantı kesilirse açık sayfa yeniden dener; sayfa kapatıldıysa aynı dosyalar yeniden seçildiğinde sunucuda hash'i doğrulanan parçalar kullanılır. Tam dosya SHA-256 doğrulamasından sonra hedefe yayınlanır. Alıcı durdurulup yeniden başlatılırsa yeni eşleştirme gerekir. Sertifika ve güvenlik duvarı ayarları uygulama tarafından değiştirilmez.

Fiziksel cihaz adımları ve sertifika ön koşulları için [iPhone test planına](docs/iphone-testleri.md) bakın. Kablosuz aktarımın fiziksel iPhone ve 5 GB üzeri dosya sonuçları henüz bekliyor. Oturum iki saat sürer; tekrar eşleştirme için alıcı yeniden başlatılmalıdır. Aktarım başına dosya sınırı 100 GiB'dir. Çökme sonrası tamamlanmış dosyanın sonucunu tekrar bildirme garantisi yoktur; mevcut hedef dosyalar üzerine yazılmaz.

Wi-Fi yarım aktarımları son etkinlikten itibaren 7 gün saklanır. Alıcı açıldığında ve dosya seçildiğinde süresi dolmuş, etkin olmayan yarım aktarımlar temizlenir. Yarım aktarımlar için uygulama genelinde ayrılan dosya boyutu toplamı en fazla 200 GiB'dir; kotayı aşan yeni aktarım reddedilir. Başarıyla doğrulanıp yayımlanan aktarımın parçaları silinir. Önceden tamamlanan bir aktarım tekrar seçildiğinde hedef dosyanın boyutu ve SHA-256 özeti yeniden doğrulanır; dosya yoksa veya değişmişse yeniden aktarılır ve mevcut dosyalar korunur.

Bekleyen doğrulamalar: temiz bir Windows kurulumunda taşınabilir EXE'nin çalıştırılması ve gerçek iPhone Safari ile 5 GB üzeri dosyada kesintiden devam testi.

Donanımsız testler: `dotnet run --project tests/Aktarim.Core.Tests -c Release`; Release derlemesinden sonra `powershell -ExecutionPolicy Bypass -File tests/usb-ui.ps1 -Language TR` (ve `EN`) ve `tests/usb-previews.ps1`. `tests/lan-ui.ps1 -Mode HTTPS` ve `-Mode HTTP` gerçek WPF onay penceresi ile Chrome/Playwright kullanır; Playwright, jsQR, pngjs ve Chrome gerektirir, test sertifikasına yalnızca test tarayıcısı güvenir. Test ekrandaki QR görüntüsünü çözümler; fiziksel kamera okutması değildir.
