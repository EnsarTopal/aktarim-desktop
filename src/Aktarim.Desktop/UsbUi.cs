using Aktarim.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Globalization;
using System.Security.Cryptography;

namespace Aktarim.Desktop;

public partial class MainWindow
{
    private string? fakeRoot;
    private string usbPartialDirectory = "";
    private List<IUsbSource> sources = [];
    private IUsbSource? selectedSource;
    private IReadOnlyList<UsbItem> items = [];
    private readonly HashSet<UsbItem> selectedItems = [];
    private UsbItem? selectionAnchor;
    private readonly List<string> backupDiagnostics = [];
    private readonly List<(bool Success, string Path, bool HistoryError)> jobs = [];
    private bool busy, listView;
    private string state = "", query = "", activeName = "", failureDetail = "";
    private int filter, galleryPage, sort;
    private long current, total;
    private TextBlock? statusText, selectionText, countText;
    private Panel? gallery;
    private Button? copyButton;
    private const int PageSize = 100;
    private const int PreviewPhotoLimit = 12 * 1024 * 1024, ThumbnailLimit = 4 * 1024 * 1024;
    private readonly SemaphoreSlim deviceGate = new(1, 1);
    private CancellationTokenSource? previewCancellation;
    private CancellationTokenSource? backupCancellation;
    private bool manualCopy;
    private List<UsbItem>? activeQueue;
    private TaskCompletionSource<bool>? backupResume;
    private bool backupPauseRequested, backupClosing;
    private readonly Dictionary<UsbItem, BitmapSource?> previewCache = [];
    private readonly List<(UsbItem Item, Image Image, UIElement Fallback)> previewTiles = [];
    private ScrollViewer? previewScroll;
    private Brush Brush(string key) => (Brush)FindResource("Aktarim." + key);
    private string Size(long bytes)
    {
        decimal value = bytes; string[] units = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (value >= 1000 && i < units.Length - 1) { value /= 1000; i++; }
        return value.ToString(i == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo(settings.Language == "tr" ? "tr-TR" : "en-US")) + " " + units[i];
    }
    private TextBlock Label(string text, int size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap };
    private Border Card(UIElement child) => new() { Child = child, Background = Brush("Surface"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12) };
    private Button MakeButton(string text, string id, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), IsEnabled = !busy };
        if (primary) button.Style = (Style)FindResource("Aktarim.PrimaryButton");
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => await action();
        return button;
    }
    private static void Add(Grid grid, UIElement child, int row, int column = 0) { Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child); }
    private void RenderUsb()
    {
        UsbHost.RowDefinitions.Clear();
        UsbHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        UsbHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        UsbHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(24, 18, 24, 14) };
        var overline = Label(T("USB AKTARIMI", "USB IMPORT"), 11, true); overline.Foreground = Brush("Accent");
        heading.Children.Add(overline);
        heading.Children.Add(Label(T("iPhone’dan içe aktar", "Import from iPhone"), 30, true));
        var intro = Label(T("Fotoğraf ve videoları seç, hedefi kontrol et ve kopyalamayı başlat.", "Select photos and videos, review the destination, and start copying.")); intro.Foreground = Brush("Muted"); heading.Children.Add(intro);
        Add(UsbHost, heading, 0);

        var body = new StackPanel { Margin = new Thickness(24, 0, 24, 16) };
        body.Children.Add(HistoryWarning());
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        previewScroll = scroll;
        scroll.ScrollChanged += (_, _) => SchedulePreviews();
        AutomationProperties.SetAutomationId(scroll, "UsbScroll"); Add(UsbHost, scroll, 1);
        var device = new Grid(); device.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); device.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var deviceInfo = new StackPanel();
        deviceInfo.Children.Add(Label(selectedSource?.Name ?? T("USB aygıtı bekleniyor", "Waiting for USB device"), 16, true));
        var hint = Label(fakeRoot != null ? T("Test kaynağı · fiziksel aygıt değil", "Test source · not a physical device") : T("iPhone’un kilidini aç ve sorulursa bu bilgisayara güven.", "Unlock the iPhone and trust this computer if prompted."), 12); hint.Foreground = Brush("Muted"); deviceInfo.Children.Add(hint);
        Add(device, deviceInfo, 0);
        var deviceActions = new StackPanel { Orientation = Orientation.Horizontal };
        if (sources.Count > 0)
        {
            var devices = new ComboBox { ItemsSource = sources.Select(source => source.Name).ToList(), SelectedIndex = sources.IndexOf(selectedSource!), Width = 135, Margin = new Thickness(0, 0, 8, 0), IsEnabled = !busy };
            AutomationProperties.SetAutomationId(devices, "Devices");
            devices.SelectionChanged += (_, _) => { selectedSource = devices.SelectedIndex >= 0 ? sources[devices.SelectedIndex] : null; items = []; selectedItems.Clear(); Render(); };
            deviceActions.Children.Add(devices);
        }
        deviceActions.Children.Add(MakeButton(T("Aygıtları bul", "Find devices"), "FindDevices", FindSources));
        if (sources.Count > 0) deviceActions.Children.Add(MakeButton(T("Medyayı tara", "Scan media"), "ScanMedia", Scan, true));
        Add(device, deviceActions, 0, 1); body.Children.Add(Card(device));
        statusText = Label("", 13); statusText.Margin = new Thickness(0, 0, 0, 8); AutomationProperties.SetAutomationId(statusText, "UsbStatus"); body.Children.Add(statusText); UpdateStatus();
        if (backupDiagnostics.Count > 0) { var diagnostics = new Expander { Header = T("Yedekleme tanılama kaydı", "Backup diagnostics"), Content = Label(string.Join("\n", backupDiagnostics)), Margin = new Thickness(0, 0, 0, 8) }; AutomationProperties.SetAutomationId(diagnostics, "BackupDiagnostics"); body.Children.Add(diagnostics); }

        if (items.Count > 0)
        {
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            string[] labels = [T("Tümü", "All"), T("Fotoğraflar", "Photos"), T("Videolar", "Videos")];
            for (int i = 0; i < labels.Length; i++)
            {
                int type = i;
                var button = MakeButton(labels[i], "Filter" + i, () => { filter = type; galleryPage = 0; Render(); return Task.CompletedTask; }, filter == i);
                button.IsEnabled = !busy || backupResume != null;
                toolbar.Children.Add(button);
            }
            var search = new TextBox { Text = query, Width = 205, MinHeight = 36, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 8, 0), ToolTip = T("Dosya adı ara", "Search filename") };
            AutomationProperties.SetName(search, T("Dosya adı ara", "Search filename")); AutomationProperties.SetAutomationId(search, "SearchFiles");
            var searchBox = new Grid(); searchBox.Children.Add(search);
            var placeholder = Label(T("Dosya adı ara", "Search filename"), 12); placeholder.Foreground = Brush("Muted"); placeholder.Margin = new Thickness(9, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; placeholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed; searchBox.Children.Add(placeholder);
            search.TextChanged += (_, _) => { query = search.Text; placeholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed; galleryPage = 0; UpdateGallery(); };
            toolbar.Children.Add(searchBox);
            var sorting = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 0), IsEnabled = !busy || backupResume != null,
                ItemsSource = new[] { T("Sıralama: varsayılan", "Sort: default"), T("Tarih: yeni önce", "Date: newest"), T("Tarih: eski önce", "Date: oldest"), T("Boyut: büyük önce", "Size: largest"), T("Boyut: küçük önce", "Size: smallest") }, SelectedIndex = sort };
            AutomationProperties.SetName(sorting, T("Medya sıralaması", "Media sort order")); AutomationProperties.SetAutomationId(sorting, "SortFiles");
            sorting.ToolTip = T("Çekim tarihi yoksa sıralamada dosya değişiklik tarihi kullanılır; klasörleme için kullanılmaz.", "When capture date is unavailable, sorting uses the file modification date; folder organization does not.");
            sorting.SelectionChanged += (_, _) => { sort = Math.Max(0, sorting.SelectedIndex); galleryPage = 0; UpdateGallery(); }; toolbar.Children.Add(sorting);
            var toggleView = MakeButton(listView ? T("Galeri", "Gallery") : T("Liste", "List"), "ToggleView", () => { listView = !listView; galleryPage = 0; Render(); return Task.CompletedTask; }); toggleView.IsEnabled = !busy || backupResume != null; toolbar.Children.Add(toggleView);
            body.Children.Add(toolbar);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            countText = Label("", 12); countText.Foreground = Brush("Muted"); countText.Margin = new Thickness(0, 8, 16, 0); actions.Children.Add(countText);
            var selectVisible = MakeButton(T("Görünenleri seç", "Select visible"), "SelectVisible", () => { foreach (var item in Visible()) selectedItems.Add(item); UpdateGallery(); return Task.CompletedTask; }); selectVisible.IsEnabled = !busy || backupResume != null; actions.Children.Add(selectVisible);
            var clearSelection = MakeButton(T("Seçimi temizle", "ClearSelection"), "ClearSelection", () => { selectedItems.Clear(); UpdateGallery(); return Task.CompletedTask; }); clearSelection.IsEnabled = !busy || backupResume != null; actions.Children.Add(clearSelection);
            body.Children.Add(actions);
            gallery = listView ? new StackPanel() : new WrapPanel();
            body.Children.Add(gallery);
            var paging = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var previous = MakeButton(T("Önceki", "Previous"), "PreviousPage", () => { galleryPage = Math.Max(0, galleryPage - 1); UpdateGallery(); return Task.CompletedTask; }); previous.IsEnabled = !busy || backupResume != null; paging.Children.Add(previous);
            var next = MakeButton(T("Sonraki", "Next"), "NextPage", () => { if ((galleryPage + 1) * PageSize < Visible().Count) galleryPage++; UpdateGallery(); return Task.CompletedTask; }); next.IsEnabled = !busy || backupResume != null; paging.Children.Add(next);
            body.Children.Add(paging);
        }
        else { gallery = null; countText = null; }

        var bottom = new Grid { Margin = new Thickness(24, 12, 24, 12) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var target = new StackPanel();
        selectionText = Label("", 14, true); AutomationProperties.SetAutomationId(selectionText, "SelectionSummary"); target.Children.Add(selectionText);
        var path = new TextBlock { Text = T("Hedef klasör · ", "Destination · ") + settings.Destination, Foreground = Brush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = settings.Destination };
        AutomationProperties.SetName(path, T("Hedef klasör: ", "Destination: ") + settings.Destination); target.Children.Add(path); Add(bottom, target, 0);
        var bottomActions = new StackPanel { Orientation = Orientation.Horizontal };
        bottomActions.Children.Add(MakeButton(T("Hedefi değiştir", "Change destination"), "ChooseDestination", () => { ChooseFolder(); return Task.CompletedTask; }));
        if (backupCancellation == null)
        {
            var backup = MakeButton(T("Yeni dosyaları yedekle", "Back up new files"), "BackupNew", BackupNew); backup.IsEnabled = !busy && items.Count > 0; bottomActions.Children.Add(backup);
        }
        else
        {
            var pause = MakeButton(backupResume != null ? (manualCopy ? T("Kopyalamaya devam et", "Resume copying") : T("Yedeklemeye devam et", "Resume backup")) : (manualCopy ? T("Kopyalamayı durdur", "Pause copying") : T("Yedeklemeyi durdur", "Pause backup")), backupResume != null ? "ResumeBackup" : "PauseBackup", () =>
            {
                if (backupResume != null) { foreach (var item in items.Where(selectedItems.Contains)) if (activeQueue != null && !activeQueue.Contains(item)) activeQueue.Add(item); selectedItems.Clear(); backupResume.TrySetResult(true); }
                else { backupPauseRequested = true; state = "pausing"; UpdateStatus(); }
                return Task.CompletedTask;
            });
            pause.IsEnabled = backupResume != null || !backupPauseRequested;
            bottomActions.Children.Add(pause);
        }
        copyButton = MakeButton(T("Seçileni kopyala", "Copy selected"), "CopySelected", CopySelected, true); bottomActions.Children.Add(copyButton); Add(bottom, bottomActions, 0, 1);
        var bottomBorder = new Border { Background = Brush("Sidebar"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        Add(UsbHost, bottomBorder, 2);
        if (items.Count > 0) UpdateGallery(); else UpdateSelection();
    }
    private List<UsbItem> Visible()
    {
        var matching = items.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) && (filter == 0 || (filter == 2) == IsVideo(x.Name)));
        return (sort switch { 1 => matching.OrderBy(x => (x.Taken ?? x.SortDate) == null).ThenByDescending(x => x.Taken ?? x.SortDate), 2 => matching.OrderBy(x => (x.Taken ?? x.SortDate) == null).ThenBy(x => x.Taken ?? x.SortDate), 3 => matching.OrderByDescending(x => x.Length), 4 => matching.OrderBy(x => x.Length), _ => matching }).ToList();
    }
    private static bool IsVideo(string name) => new[] { ".mov", ".mp4", ".m4v", ".avi" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    private void UpdateGallery()
    {
        if (gallery == null) return;
        StopPreviews(); previewTiles.Clear();
        var visible = Visible();
        if (selectionAnchor != null && !visible.Contains(selectionAnchor)) selectionAnchor = null;
        galleryPage = Math.Min(galleryPage, Math.Max(0, (visible.Count - 1) / PageSize));
        gallery.Children.Clear();
        if (countText != null) countText.Text = T("Medya", "Media") + $" · {visible.Count:N0}  ·  {galleryPage + 1}/{Math.Max(1, (visible.Count + PageSize - 1) / PageSize)}";
        if (visible.Count == 0) gallery.Children.Add(Label(T("Bu filtreyle eşleşen dosya yok.", "No files match this filter.")));
        var indexed = items.ToList();
        foreach (var item in visible.Skip(galleryPage * PageSize).Take(PageSize))
        {
            int index = indexed.IndexOf(item);
            var content = new StackPanel();
            var badge = Label(Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant(), 12, true); badge.Foreground = Brush("Accent");
            var preview = Label(T("Önizleme yok", "Preview unavailable"), 12); preview.Foreground = Brush("Muted");
            var thumb = new Grid { Background = Brush("Raised"), Height = listView ? 45 : 75, Margin = new Thickness(0, 0, 0, 8) };
            var fallback = new StackPanel(); fallback.Children.Add(badge); fallback.Children.Add(preview); thumb.Children.Add(fallback);
            var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Hidden }; thumb.ClipToBounds = true; thumb.Children.Add(image); content.Children.Add(thumb);
            AutomationProperties.SetAutomationId(image, "Preview-" + index);
            previewTiles.Add((item, image, fallback));
            var name = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, Foreground = Brush("Text") }; content.Children.Add(name);
            var size = Label(Size(item.Length), 12); size.Foreground = Brush("Muted"); content.Children.Add(size);
            var date = Label(item.Taken?.ToString("d") ?? T("Tarih bilinmiyor", "Date unknown"), 12); date.Foreground = Brush("Muted"); content.Children.Add(date);
            var check = new CheckBox { Content = content, IsChecked = selectedItems.Contains(item), Width = listView ? 440 : 160, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), Background = Brush("Surface"), Foreground = Brush("Text"), BorderBrush = Brush("Stroke"), IsEnabled = !busy || backupResume != null };
            check.Style = (Style)FindResource("Aktarim.MediaTile");
            AutomationProperties.SetName(check, item.Name);
            AutomationProperties.SetAutomationId(check, "File-" + index);
            void SelectRange(RoutedEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && selectionAnchor != null)
                {
                    int first = visible.IndexOf(selectionAnchor), last = visible.IndexOf(item);
                    if (first >= 0 && last >= 0) { foreach (var rangeItem in visible.Skip(Math.Min(first, last)).Take(Math.Abs(first - last) + 1)) selectedItems.Add(rangeItem); e.Handled = true; UpdateGallery(); return; }
                }
                selectionAnchor = item;
            }
            check.PreviewMouseLeftButtonDown += (_, e) => SelectRange(e);
            check.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) SelectRange(e); };
            check.Checked += (_, _) => { selectedItems.Add(item); UpdateSelection(); };
            check.Unchecked += (_, _) => { selectedItems.Remove(item); UpdateSelection(); };
            gallery.Children.Add(check);
        }
        UpdateSelection();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, SchedulePreviews);
    }
    private void StopPreviews() { previewCancellation?.Cancel(); previewCancellation = null; }
    private void SchedulePreviews()
    {
        StopPreviews();
        if (busy || page != 1 || previewScroll == null || !previewScroll.IsLoaded) return;
        var viewport = new Rect(0, 0, previewScroll.ViewportWidth, previewScroll.ViewportHeight);
        var tiles = previewTiles.Where(tile => tile.Image.IsLoaded && viewport.IntersectsWith(tile.Image.TransformToAncestor(previewScroll).TransformBounds(new Rect(tile.Image.RenderSize)))).ToArray();
        var cancellation = new CancellationTokenSource(); previewCancellation = cancellation;
        _ = LoadPreviews(tiles, cancellation.Token);
    }
    private async Task LoadPreviews((UsbItem Item, Image Image, UIElement Fallback)[] tiles, CancellationToken token)
    {
        try
        {
            foreach (var tile in tiles)
            {
                token.ThrowIfCancellationRequested();
                if (!previewCache.TryGetValue(tile.Item, out var bitmap))
                {
                    await deviceGate.WaitAsync(token);
                    try { bitmap = await Task.Run(() => ReadPreview(tile.Item, token), token); }
                    finally { deviceGate.Release(); }
                    token.ThrowIfCancellationRequested();
                    if (previewCache.Count >= 60) previewCache.Clear();
                    previewCache[tile.Item] = bitmap;
                }
                if (bitmap != null) { tile.Image.Source = bitmap; AutomationProperties.SetName(tile.Image, T("Önizleme yüklendi", "Preview loaded")); tile.Image.Visibility = Visibility.Visible; tile.Fallback.Visibility = Visibility.Collapsed; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private static BitmapSource? ReadPreview(UsbItem item, CancellationToken token)
    {
        BitmapSource? Read(Func<Stream> open, int limit)
        {
            try
            {
                using var input = open(); using var output = new MemoryStream(); var buffer = new byte[65536];
                // Finish a bounded WPD preview before honoring cancellation: closing it mid-read can leave the iPhone driver unable to open the next file.
                while (true) { int read = input.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)); if (read == 0) break; output.Write(buffer, 0, read); if (output.Length > limit) return null; }
                output.Position = 0;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 320; image.StreamSource = output; image.EndInit(); image.Freeze(); return image;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }
        var thumbnail = item.OpenThumbnail == null ? null : Read(item.OpenThumbnail, ThumbnailLimit);
        token.ThrowIfCancellationRequested();
        return thumbnail ?? (!IsVideo(item.Name) && item.Length <= PreviewPhotoLimit ? Read(item.Open, PreviewPhotoLimit) : null);
    }
    private void UpdateSelection()
    {
        if (selectionText == null) return;
        var visibleSet = Visible().Skip(galleryPage * PageSize).Take(PageSize).ToHashSet(); int hidden = selectedItems.Count(x => !visibleSet.Contains(x));
        long bytes = selectedItems.Sum(x => x.Length);
        selectionText.Text = $"{selectedItems.Count:N0} " + T("dosya seçildi", "files selected") + " · " + Size(bytes) + (hidden > 0 ? $" · {hidden:N0} " + T("görünüm dışında", "outside view") : "");
        if (copyButton != null) copyButton.IsEnabled = !busy && selectedItems.Count > 0;
    }
    private void UpdateStatus()
    {
        if (statusText == null) return;
        statusText.Text = state switch
        {
            "finding" => T("Aygıtlar aranıyor…", "Searching for devices…"),
            "none" => T("Aygıt bulunamadı. Veri kablosunu, kilidi, güven onayını ve sürücüyü kontrol et.", "No device found. Check the data cable, lock, trust prompt and driver."),
            "scanning" => T("Medya taranıyor…", "Scanning media…"),
            "empty" => T("Görünür fotoğraf veya video bulunamadı.", "No visible photos or videos found."),
            "copying" => T("Kopyalanıyor: ", "Copying: ") + activeName + " · " + Size(current) + " / " + Size(total),
            "verifying" => T("Yerel kopya doğrulanıyor: ", "Verifying local copy: ") + activeName,
            "completed" => T("Yerel kopya doğrulandı: ", "Local copy verified: ") + activeName,
            "backupdone" => T("Yedekleme bitti: ", "Backup complete: ") + activeName,
            "copydone" => T("Seçili aktarım bitti: ", "Selected transfer complete: ") + activeName,
            "checking" => T("Önceki yedek doğrulanıyor: ", "Checking previous backup: ") + activeName + (total > 0 ? " · " + Size(current) + " / " + Size(total) : ""),
            "resumecheck" => T("Kaydedilen bölüm kaynakla doğrulanıyor: ", "Checking saved portion against source: ") + activeName + " · " + Size(current) + " / " + Size(total),
            "historyerror" => T("Kopya doğrulandı ancak geçmiş kaydedilemedi; dosya sonraki yedeklemede tekrar kopyalanabilir: ", "Copy verified, but history could not be saved; the file may be copied again on the next backup: ") + activeName,
            "pausedconnection" => T("Bağlantı kesildi veya kaynak yarım okundu. Yeniden bağlayın, aygıtları bulun, medyayı tarayın ve yedeklemeyi başlatın: ", "Connection lost or source ended early. Reconnect, find devices, scan media, then restart the backup: ") + activeName + failureDetail,
            "pausedmanual" => T("Bağlantı kesildi veya kaynak yarım okundu; tamamlananlar korundu, yarım dosyayı yeniden seçin: ", "Connection lost or source ended early; completed files are preserved, select the partial file again: ") + activeName + failureDetail,
            "pauseddisk" => T("Hedef disk dolu; kuyruk duraklatıldı. Tamamlanan dosyalar korundu: ", "Destination disk is full; queue paused. Completed files are preserved: ") + activeName,
            "pausing" => manualCopy ? T("Kopyalama durduruluyor…", "Pausing copying…") : T("Yedekleme durduruluyor…", "Pausing backup…"),
            "pauseduser" => (manualCopy ? T("Kopyalama durduruldu: ", "Copying paused: ") : T("Yedekleme durduruldu: ", "Backup paused: ")) + activeName + T(" · Yeni dosyaları seçip devam edebilirsin.", " · Select more files, then resume."),
            "backupissues" => T("Bazı dosyalar kopyalanamadı; diğer dosyalar işlendi: ", "Some files could not be copied; other files were processed: ") + activeName,
            "failed" => T("Aktarım başarısız veya bağlantı kesildi. Yarım dosya tamamlanmadı; yeniden seçip baştan deneyin: ", "Transfer failed or connection was interrupted. The partial file was not completed; select it again to restart: ") + activeName,
            "error" => T("Aygıt işlemi başarısız. Kabloyu, kilidi ve güven onayını kontrol edin: ", "Device operation failed. Check cable, lock and trust approval: ") + activeName,
            _ => ""
        };
        statusText.Visibility = string.IsNullOrEmpty(statusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
    }
    private async Task FindSources()
    {
        busy = true; state = "finding"; Render();
        await deviceGate.WaitAsync();
        try
        {
            var found = await Task.Run(() => fakeRoot == null ? UsbSources.Find().ToList() : new List<IUsbSource> { new FakeUsbSource(fakeRoot) });
            foreach (var old in sources) old.Dispose();
            sources = found; selectedSource = sources.FirstOrDefault(); items = []; selectedItems.Clear();
            state = sources.Count == 0 ? "none" : "";
        }
        catch (Exception) { state = "error"; activeName = ""; }
        finally { deviceGate.Release(); previewCache.Clear(); busy = false; Render(); }
    }
    private async Task Scan()
    {
        if (selectedSource == null) return;
        busy = true; state = "scanning"; Render();
        await deviceGate.WaitAsync();
        try { items = await Task.Run(selectedSource.Scan); selectedItems.Clear(); galleryPage = 0; state = items.Count == 0 ? "empty" : ""; }
        catch (Exception) { items = []; state = "error"; activeName = ""; }
        finally { deviceGate.Release(); previewCache.Clear(); busy = false; Render(); }
    }
    private async Task CopySelected()
    {
        var selected = items.Where(selectedItems.Contains).ToArray();
        if (selected.Length == 0) return;
        selectedItems.Clear();
        await CopyItems(selected, false);
    }
    private Task BackupNew() => CopyItems(items.ToArray(), true);
    private async Task BackupCheckpoint()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(BackupCheckpoint).Task.Unwrap(); return; }
        if (!backupPauseRequested) return;
        string previousState = state;
        backupResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        state = "pauseduser"; Render();
        if (!await backupResume.Task || backupClosing) throw new OperationCanceledException();
        backupResume = null; backupPauseRequested = false; state = previousState == "pausing" ? "copying" : previousState; Render();
    }
    private static async Task<string> HashSource(UsbItem item, IProgress<long> progress, CancellationToken token, Func<Task> checkpoint)
    {
        using var input = item.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long length = 0; int read;
        while (true) { await checkpoint(); read = await input.ReadAsync(buffer, token); if (read == 0) break; length = checked(length + read); if (length > item.Length) throw new InvalidDataException("Source exceeded expected length."); hash.AppendData(buffer, 0, read); progress.Report(length); }
        if (length != item.Length) throw new UsbSourceEndedException(length, item.Length);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private async Task CopyItems(UsbItem[] selected, bool onlyNew)
    {
        if (selected.Length == 0) return;
        string destination = settings.Destination;
        var queue = selected.ToList(); activeQueue = queue;
        bool dateFolders = settings.UsbDateFolders;
        int copied = 0, skipped = 0, failures = 0; bool paused = false;
        backupDiagnostics.Clear(); failureDetail = "";
        manualCopy = !onlyNew; backupClosing = false; backupPauseRequested = false; backupCancellation = new CancellationTokenSource();
        busy = true; Render();
        await deviceGate.WaitAsync();
        try
        {
            for (int index = 0; index < queue.Count; index++)
            {
                var item = queue[index];
                string identity = item.SourceLocator ?? item.SourceKey ?? Guid.NewGuid().ToString("N");
                string? published = null;
                try
                {
                    var token = backupCancellation?.Token ?? CancellationToken.None;
                    token.ThrowIfCancellationRequested();
                    await BackupCheckpoint();
                    activeName = item.Name; current = 0; total = item.Length; state = "checking"; UpdateStatus();
                    string decision = "manual";
                    {
                        try { decision = await usbHistory.CheckAsync(item.SourceKey, item.Length, destination, item.SourceLocator, cancellationToken: token); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { decision = "history-unavailable"; }
                        if (decision == "content-required")
                        {
                            var hashProgress = new Progress<long>(value => { current = value; UpdateStatus(); });
                            string sourceHash;
                            for (int attempt = 0; ; attempt++)
                            {
                                try { sourceHash = await Task.Run(() => HashSource(item, hashProgress, token, BackupCheckpoint), token); break; }
                                catch (Exception e) when (attempt < 4 && !TransferErrors.IsDiskFull(e) && e is not ArgumentException and not UnauthorizedAccessException and not OperationCanceledException)
                                { await Task.Delay(500 * (1 << attempt), token); }
                            }
                            try { decision = await usbHistory.CheckAsync(null, item.Length, destination, item.SourceLocator, sourceHash, token); }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { decision = "history-unavailable"; }
                        }
                    }
                    {
                        string reason = decision switch { "verified" => T("doğrulandı, atlandı", "verified, skipped"), "identity-unknown" => T("kaynak kimliği veya değişim bilgisi belirsiz", "source identity or modification is uncertain"), "no-record" => T("bu kaynak sürümü ve hedef için geçmiş kaydı yok", "no history for this source version and destination"), "source-changed" => T("kaynak içeriği değişmiş", "source content changed"), "copy-missing-or-changed" => T("hedef kopya eksik, taşınmış veya değişmiş", "destination copy is missing, moved or changed"), _ => T("geçmiş kullanılamıyor", "history unavailable") };
                        backupDiagnostics.Add(item.Name + ": " + reason);
                    }
                    if (decision == "verified") { skipped++; continue; }
                    current = 0; total = item.Length; state = "copying"; UpdateStatus();
                    var progress = new Progress<long>(value => { current = value; UpdateStatus(); });
                    string target = dateFolders && item.Taken is { } date ? Path.Combine(destination, date.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), date.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), date.ToString("dd", System.Globalization.CultureInfo.InvariantCulture)) : destination;
                    string? hash = null;
                    string path = "";
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            path = await Task.Run(async () =>
                            {
                                using var source = item.Open();
                                Action verify = () => Dispatcher.Invoke(() => { state = "verifying"; UpdateStatus(); });
                                return await UsbResumableCopy.CopyAsync(source, item.Length, target, item.Name, usbPartialDirectory, identity, token, progress, verify, value => hash = value,
                                    checking => Dispatcher.Invoke(() => { state = checking ? "resumecheck" : "copying"; UpdateStatus(); }), BackupCheckpoint);
                            });
                            break;
                        }
                        catch (Exception e) when (attempt < 4 && !TransferErrors.IsDiskFull(e) && e is not ArgumentException and not UnauthorizedAccessException and not OperationCanceledException)
                        { await Task.Delay(500 * (1 << attempt), token); state = "copying"; UpdateStatus(); }
                    }
                    published = path;
                    await usbHistory.AddAsync(item.SourceKey, item.Length, destination, path, hash ?? throw new IOException("Missing verified hash."), item.SourceLocator);
                    copied++;
                    state = "completed"; activeName = item.Name; jobs.Add((true, path, false));
                    await BackupCheckpoint();
                }
                catch (OperationCanceledException) when (backupClosing)
                {
                    return;
                }
                catch (Exception e)
                {
                    failures++; activeName = item.Name; jobs.Add((published != null, published ?? item.Name, published != null));
                    if (published != null) { state = "historyerror"; paused = true; break; }
                    if (TransferErrors.IsDiskFull(e)) { state = "pauseddisk"; paused = true; break; }
                    if (TransferErrors.IsDisconnected(e))
                    {
                        if (e.GetBaseException() is UsbSourceEndedException ended)
                        {
                            failureDetail = $" · {ended.Actual:N0} / {ended.Expected:N0} " + T("bayt okundu", "bytes read");
                            backupDiagnostics.Add(item.Name + ": " + (ended.ValidatingPartial ? T("kayıtlı bölüm doğrulanırken", "while checking the saved portion") : T("kaynak okunurken", "while reading the source")) + $" {ended.Actual:N0} / {ended.Expected:N0} " + T("bayt okundu", "bytes read"));
                        }
                        else { var cause = e.GetBaseException(); failureDetail = $" · {cause.GetType().Name} (0x{cause.HResult:X8})"; backupDiagnostics.Add(item.Name + ": " + failureDetail); }
                        state = onlyNew ? "pausedconnection" : "pausedmanual"; paused = true; break;
                    }
                    var error = e.GetBaseException();
                    backupDiagnostics.Add(item.Name + ": " + T("dosya işlenemedi", "file could not be processed") + $" ({error.GetType().Name}, 0x{error.HResult:X8}: {error.Message})");
                    state = "failed";
                }
                UpdateStatus();
            }
            if (!paused) { state = failures == 0 ? (onlyNew ? "backupdone" : "copydone") : "backupissues"; activeName = $"{copied:N0} " + T("kopyalandı", "copied") + $", {skipped:N0} " + T("atlandı", "skipped") + (failures > 0 ? $", {failures:N0} " + T("hata", "failed") : ""); }
        }
        finally { deviceGate.Release(); backupCancellation?.Dispose(); backupCancellation = null; backupResume = null; activeQueue = null; backupPauseRequested = false; busy = false; await RefreshHistory(); Render(); }
    }
}
