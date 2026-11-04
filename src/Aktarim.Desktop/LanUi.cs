using Microsoft.Win32;
using QRCoder;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Aktarim.Desktop;

public partial class MainWindow
{
    private LanReceiver? lan;
    private X509Certificate2? lanCertificate;
    private string lanPfx = "", lanError = "", selectedLan = "";
    private bool lanStarting, lanHttp;
    private void RenderLan()
    {
        if (lan != null)
        {
            Paragraph(lanHttp ? T("Şifresiz yerel aktarım (HTTP): ağdaki kişiler verileri okuyabilir veya değiştirebilir.", "Unencrypted local transfer (HTTP): people on the network can read or modify data.") : T("Şifreli yerel aktarım (HTTPS)", "Encrypted local transfer (HTTPS)"));
            Paragraph(T("iPhone kamerasıyla QR kodunu okutun, Safari'de açın ve isteği bu bilgisayarda onaylayın. QR beş dakika geçerlidir ve bir kez kullanılabilir.", "Scan the QR code with the iPhone camera, open it in Safari and approve the request on this computer. The QR expires after five minutes and can be used once."));
            using var qrData = new QRCodeGenerator().CreateQrCode(lan.PairUrl, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(qrData).GetGraphic(6);
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            var qr = new Image { Source = bitmap, Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            AutomationProperties.SetName(qr, T("Eşleştirme QR kodu", "Pairing QR code")); AutomationProperties.SetAutomationId(qr, "LanQr"); ContentPanel.Children.Add(qr);
            if (fakeRoot != null && Environment.GetEnvironmentVariable("AKTARIM_LAN_TEST") == "1") { var testUrl = new TextBox { Text = lan.PairUrl, IsReadOnly = true }; AutomationProperties.SetAutomationId(testUrl, "LanTestUrl"); ContentPanel.Children.Add(testUrl); }
            Paragraph(T("Hedef: ", "Destination: ") + settings.Destination);
            Action(T("Alıcıyı kapat", "Stop receiver"), async () => { var old = lan; lan = null; await old.DisposeAsync(); lanCertificate?.Dispose(); lanCertificate = null; Render(); });
            return;
        }
        Paragraph(T("Bilgisayar ve iPhone aynı yerel ağa bağlı olmalı.", "Connect the computer and iPhone to the same local network."));
        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && LanReceiver.PrivateAddress(a.Address)).Select(a => new { Label = n.Name + " — " + a.Address, Address = a.Address.ToString() })).ToList();
        if (fakeRoot != null && Environment.GetEnvironmentVariable("AKTARIM_LAN_TEST") == "1") adapters.Insert(0, new { Label = "Test loopback", Address = "127.0.0.1" });
        Paragraph(T("Yerel ağ arayüzü", "Local network interface"));
        var networks = new ComboBox { Margin = new Thickness(0, 0, 0, 12), DisplayMemberPath = "Label", SelectedValuePath = "Address", ItemsSource = adapters };
        AutomationProperties.SetAutomationId(networks, "LanNetwork"); ContentPanel.Children.Add(networks);
        networks.SelectedValue = adapters.Any(a => a.Address == selectedLan) ? selectedLan : adapters.FirstOrDefault()?.Address;
        networks.SelectionChanged += (_, _) => selectedLan = networks.SelectedValue as string ?? "";
        selectedLan = networks.SelectedValue as string ?? "";
        var mode = new ComboBox { Margin = new Thickness(0, 0, 0, 12), ItemsSource = new[] { T("Şifreli HTTPS", "Encrypted HTTPS"), T("Şifresiz yerel aktarım (HTTP)", "Unencrypted local transfer (HTTP)") }, SelectedIndex = lanHttp ? 1 : 0 };
        AutomationProperties.SetAutomationId(mode, "LanMode"); ContentPanel.Children.Add(mode);
        mode.SelectionChanged += (_, _) => { lanHttp = mode.SelectedIndex == 1; Render(); };
        PasswordBox? password = null;
        if (lanHttp) Paragraph(T("Yalnızca güvendiğiniz yerel ağda kullanın; dosyalar ve oturum bilgileri şifrelenmez.", "Use only on a trusted local network; files and session credentials are not encrypted."));
        else
        {
            Paragraph(T("iPhone'un güvendiği ve seçili IPv4 adresini içeren özel anahtarlı PFX sertifikası gerekir.", "A PFX certificate with a private key, trusted by the iPhone and covering the selected IPv4 address, is required."));
            var pfx = new TextBox { Text = lanPfx, Margin = new Thickness(0, 0, 0, 8) }; AutomationProperties.SetAutomationId(pfx, "LanPfx"); pfx.TextChanged += (_, _) => lanPfx = pfx.Text; ContentPanel.Children.Add(pfx);
            Action(T("Sertifika seç", "Choose certificate"), () => { var dialog = new OpenFileDialog { Filter = "PFX (*.pfx;*.p12)|*.pfx;*.p12" }; if (dialog.ShowDialog(this) == true) { lanPfx = dialog.FileName; Render(); } });
            Paragraph(T("PFX parolası", "PFX password"));
            password = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) }; AutomationProperties.SetAutomationId(password, "LanPassword"); ContentPanel.Children.Add(password);
        }
        Paragraph(T("Hedef: ", "Destination: ") + settings.Destination);
        Action(T("Hedefi değiştir", "Change destination"), ChooseFolder);
        if (!string.IsNullOrEmpty(lanError)) Paragraph(lanError);
        var start = new Button { Content = T("Alma oturumunu aç", "Start receiver"), IsEnabled = !lanStarting, Margin = new Thickness(0, 0, 0, 12) }; AutomationProperties.SetAutomationId(start, "StartLan"); ContentPanel.Children.Add(start);
        start.Click += async (_, _) =>
        {
            try
            {
                lanStarting = true; start.IsEnabled = false; lanError = "";
                if (!IPAddress.TryParse(selectedLan, out var address) || !LanReceiver.PrivateAddress(address)) throw new ArgumentException("address");
                if (!lanHttp)
                {
                    lanCertificate = X509CertificateLoader.LoadPkcs12FromFile(lanPfx, password!.Password, X509KeyStorageFlags.DefaultKeySet);
                    if (!lanCertificate.HasPrivateKey || DateTime.Now < lanCertificate.NotBefore || DateTime.Now > lanCertificate.NotAfter || !lanCertificate.MatchesHostname(address.ToString(), true, false)) throw new ArgumentException("certificate");
                }
                var receiver = new LanReceiver(settings.Destination, () => Dispatcher.BeginInvoke(() => {
                    if (lan == null || !lan.HasPending) return;
                    bool accept = MessageBox.Show(this, T("iPhone bağlantısına izin verilsin mi?", "Allow this iPhone connection?"), T("Eşleştirme isteği", "Pairing request"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    lan.Decide(accept);
                }));
                try { await receiver.StartAsync(address, lanCertificate); lan = receiver; }
                catch { await receiver.DisposeAsync(); throw; }
            }
            catch (Exception) { lanCertificate?.Dispose(); lanCertificate = null; lanError = T("Alıcı başlatılamadı. Ağı, sertifikayı ve parolayı kontrol edin.", "Could not start receiver. Check the network, certificate and password."); }
            finally { lanStarting = false; Render(); }
        };
    }
}
