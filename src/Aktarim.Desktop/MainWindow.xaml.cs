using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using Aktarim.Core;

namespace Aktarim.Desktop;

public partial class MainWindow : Window
{
    private readonly string settingsFile;
    private Settings settings;
    private readonly UsbHistory usbHistory;
    private IReadOnlyList<UsbHistoryEntry> historyEntries = [];
    private bool historyCorrupt, historyUnavailable, confirmHistoryRecovery;
    private string? historyRecoveryMessage;
    private int page;
    private string T(string tr, string en) => settings.Language == "tr" ? tr : en;
    public MainWindow() { InitializeComponent(); var args = Environment.GetCommandLineArgs(); string profile = args.Length == 5 && args[1] == "--fake-usb" ? args[4] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aktarim"); settingsFile = Path.Combine(profile, "settings.json"); usbHistory = new UsbHistory(Path.Combine(profile, "UsbHistory")); settings = Load(); if ((args.Length == 4 || args.Length == 5) && args[1] == "--fake-usb") { fakeRoot = args[2]; settings.Destination = args[3]; } if (SystemParameters.HighContrast) ApplyHighContrast(); SizeChanged += (_, _) => SideColumn.Width = new GridLength(ActualWidth < 1050 ? 160 : 204); Closed += async (_, _) => { backupClosing = true; backupCancellation?.Cancel(); backupResume?.TrySetResult(false); if (lan != null) await lan.DisposeAsync(); StopPreviews(); await deviceGate.WaitAsync(); try { foreach (var source in sources) source.Dispose(); } finally { deviceGate.Release(); } }; Render(); _ = RefreshHistory(); }
    private async Task RefreshHistory()
    {
        try { historyEntries = await usbHistory.EntriesAsync(); historyCorrupt = historyUnavailable = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { historyEntries = []; historyCorrupt = e is UsbHistoryCorruptException; historyUnavailable = true; }
        if (!busy && (page == 1 || page == 3)) Render();
    }
    private UIElement HistoryWarning()
    {
        var panel = new StackPanel();
        if (historyUnavailable)
        {
            panel.Children.Add(Label(historyCorrupt ? T("Yedekleme geçmişi bozuk. Geçmişe göre dosya atlanmaz. Bozuk dosyayı koruyarak boş geçmiş başlatabilirsiniz.", "Backup history is corrupted. Files will not be skipped based on history. You can preserve the damaged file and start an empty history.") : T("Yedekleme geçmişine erişilemiyor. Geçmişe göre dosya atlanmaz.", "Backup history is unavailable. Files will not be skipped based on history.")));
            if (historyCorrupt)
            {
                if (!confirmHistoryRecovery) panel.Children.Add(MakeButton(T("Geçmişi kurtar", "Recover history"), "RecoverHistory", () => { confirmHistoryRecovery = true; Render(); return Task.CompletedTask; }));
                else
                {
                    panel.Children.Add(Label(T("Bozuk dosya korunacak; boş geçmiş başlatılacak. Onaylıyor musunuz?", "The damaged file will be preserved and an empty history started. Confirm?")));
                    panel.Children.Add(MakeButton(T("Onayla: boş geçmiş başlat", "Confirm: start empty history"), "ConfirmHistoryRecovery", async () => { try { var archive = await usbHistory.RecoverCorruptAsync(); historyRecoveryMessage = archive == null ? T("Geçmiş zaten düzeltilmiş.", "History was already repaired.") : T("Bozuk geçmiş korundu: ", "Damaged history preserved: ") + archive; confirmHistoryRecovery = false; await RefreshHistory(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { historyRecoveryMessage = T("Kurtarma başarısız: ", "Recovery failed: ") + e.Message; Render(); } }));
                    panel.Children.Add(MakeButton(T("Vazgeç", "Cancel"), "CancelHistoryRecovery", () => { confirmHistoryRecovery = false; Render(); return Task.CompletedTask; }));
                }
            }
        }
        if (historyRecoveryMessage != null) panel.Children.Add(Label(historyRecoveryMessage));
        return panel;
    }
    private void ApplyHighContrast()
    {
        foreach (var key in new[] { "Background", "Sidebar", "Surface", "Raised" }) Resources["Aktarim." + key] = SystemColors.WindowBrush;
        foreach (var key in new[] { "Text", "Muted", "Stroke", "ControlStroke" }) Resources["Aktarim." + key] = SystemColors.WindowTextBrush;
        Resources["Aktarim.Accent"] = SystemColors.HighlightBrush; Resources["Aktarim.OnAccent"] = SystemColors.HighlightTextBrush; Resources["Aktarim.Focus"] = SystemColors.HighlightBrush;
    }
    private Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(T("Ayarlar kaydedilemedi: ", "Could not save settings: ") + e.Message, "Aktarım"); }
    }
    private void Turkish(object sender, RoutedEventArgs e) { settings.Language = "tr"; Save(); Render(); }
    private void English(object sender, RoutedEventArgs e) { settings.Language = "en"; Save(); Render(); }
    private void Render()
    {
        StopPreviews(); previewTiles.Clear(); previewScroll = null;
        Navigation.Children.Clear(); ContentPanel.Children.Clear(); UsbHost.Children.Clear();
        UsbHost.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed; RegularScroll.Visibility = page == 1 ? Visibility.Collapsed : Visibility.Visible;
        NavTitle.Text = T("ÇALIŞMA ALANI", "WORKSPACE");
        string[] names = settings.Language == "tr" ? ["Genel bakış", "USB", "Wi-Fi", "Aktarımlar", "Ayarlar"] : ["Overview", "USB", "Wi-Fi", "Transfers", "Settings"];
        string[] icons = ["⌂", "⇄", "⌁", "▤", "⚙"];
        for (int i = 0; i < names.Length; i++) { int index = i; var b = new Button { Content = icons[i] + "  " + names[i], HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10) }; if (i == page) { b.SetResourceReference(BackgroundProperty, "Aktarim.Accent"); b.SetResourceReference(ForegroundProperty, "Aktarim.OnAccent"); } AutomationProperties.SetAutomationId(b, "Nav" + i); b.Click += (_, _) => { page = index; Render(); }; Navigation.Children.Add(b); }
        switch (page)
        {
            case 0:
                Heading(T("iPhone’dan Windows’a aktar", "Transfer from iPhone to Windows"));
                Paragraph(T("Bağlantını seç ve aktarım seçeneklerini incele.", "Choose a connection and review the transfer options."));
                Action(T("USB ile içe aktar", "Import over USB"), () => { page = 1; Render(); });
                Action(T("Wi-Fi ile al", "Receive over Wi-Fi"), () => { page = 2; Render(); }); break;
            case 1:
                RenderUsb(); break;
            case 2:
                Heading(T("Wi-Fi ile al", "Receive over Wi-Fi"));
                RenderLan();
                Heading(T("Windows paylaşımına kopyala", "Copy to a Windows share"));
                Paragraph(T("1. Windows’ta özel ağ üzerinde yalnızca yetkili kullanıcıya erişim veren ayrı bir klasör paylaşımı oluştur. SMB1 ve misafir erişimini kapalı tut.", "1. On a private Windows network, create a separate folder share restricted to an authorized user. Keep SMB1 and guest access disabled."));
                Paragraph(T("2. iPhone ve bilgisayarı aynı ağa bağla. iPhone’da Dosyalar → Göz At → … → Sunucuya Bağlan yolunu aç ve smb://PC-IP adresini gir.", "2. Connect the iPhone and computer to the same network. On iPhone open Files → Browse → … → Connect to Server and enter smb://PC-IP."));
                Paragraph(T("3. Windows kullanıcı bilgilerinle giriş yap, paylaşılan klasörü aç ve dosyayı kopyala. Dosyalar uygulamasında tamamlandığını, Windows’ta dosya boyutunu ve oynatmayı kontrol et.", "3. Sign in with your Windows credentials, open the shared folder and copy the file. Check completion in Files and check file size and playback on Windows."));
                Paragraph(T("Uygulama paylaşım veya güvenlik duvarı ayarlarını değiştirmez. SMB aktarımı otomatik doğrulanmaz veya devam ettirilmez.", "The app does not change sharing or firewall settings. SMB transfers are not automatically verified or resumed.")); break;
            case 3: Heading(T("Aktarımlar", "Transfers")); ContentPanel.Children.Add(HistoryWarning()); foreach (var job in jobs.Where(x => !x.Success || x.HistoryError)) Paragraph((job.HistoryError ? T("Kopya doğrulandı, geçmiş kaydedilemedi: ", "Copy verified, history could not be saved: ") : T("Başarısız: ", "Failed: ")) + job.Path); foreach (var entry in historyEntries.Reverse()) Paragraph(T("Yerel kopya doğrulandı: ", "Local copy verified: ") + entry.Path + " · " + Size(entry.Length)); if (jobs.Count == 0 && historyEntries.Count == 0) Paragraph(T("Henüz aktarım yok.", "No transfers yet.")); break;
            case 4:
                Heading(T("Ayarlar", "Settings")); Paragraph(T("Varsayılan hedef: ", "Default destination: ") + settings.Destination);
                Action(T("Klasör seç", "Choose folder"), ChooseFolder);
                var folders = new CheckBox { Content = T("USB yedeklerini Yıl/Ay/Gün klasörlerine ayır (tarihi bilinmeyenler hedef klasöre)", "Organize USB backups into Year/Month/Day folders (unknown dates stay in destination)"), IsChecked = settings.UsbDateFolders, Margin = new Thickness(0, 0, 0, 16) };
                AutomationProperties.SetAutomationId(folders, "UsbDateFolders");
                folders.Checked += (_, _) => { settings.UsbDateFolders = true; Save(); };
                folders.Unchecked += (_, _) => { settings.UsbDateFolders = false; Save(); }; ContentPanel.Children.Add(folders);
                Paragraph(T("Dili üstteki TR ve EN düğmeleriyle değiştir.", "Change language using the TR and EN buttons above.")); break;
        }
    }
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = T("Hedef klasörü seç", "Choose destination folder") };
        if (dialog.ShowDialog(this) == true) { settings.Destination = dialog.FolderName; Save(); Render(); }
    }
    private void Heading(string text) => ContentPanel.Children.Add(new TextBlock { Text = text, FontSize = 30, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
    private void Paragraph(string text) => ContentPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
    private void Action(string text, Action action) { var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) }; button.Click += (_, _) => action(); ContentPanel.Children.Add(button); }
}
public class Settings
{
    public string Language { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    public string Destination { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Aktarim");
    public bool UsbDateFolders { get; set; }
}
