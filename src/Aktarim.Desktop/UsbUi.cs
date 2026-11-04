using Aktarim.Core;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Aktarim.Desktop;

public partial class MainWindow
{
    private string? fakeRoot;
    private List<IUsbSource> sources = [];
    private IUsbSource? selectedSource;
    private IReadOnlyList<UsbItem> items = [];
    private readonly HashSet<UsbItem> selectedItems = [];
    private readonly List<(bool Success, string Path)> jobs = [];
    private bool busy, listView;
    private string state = "", query = "", activeName = "";
    private int filter, galleryPage;
    private long current, total;
    private TextBlock? statusText, selectionText, countText;
    private Panel? gallery;
    private Button? copyButton;
    private const int PageSize = 30;
    private const int PreviewPhotoLimit = 12 * 1024 * 1024, ThumbnailLimit = 4 * 1024 * 1024;
    private readonly SemaphoreSlim deviceGate = new(1, 1);
    private CancellationTokenSource? previewCancellation;
    private readonly Dictionary<UsbItem, BitmapSource?> previewCache = [];
    private readonly List<(UsbItem Item, Image Image, UIElement Fallback)> previewTiles = [];
    private ScrollViewer? previewScroll;
    private Brush Brush(string key) => (Brush)FindResource("Aktarim." + key);
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

        if (items.Count > 0)
        {
            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            string[] labels = [T("Tümü", "All"), T("Fotoğraflar", "Photos"), T("Videolar", "Videos")];
            for (int i = 0; i < labels.Length; i++)
            {
                int type = i;
                var button = MakeButton(labels[i], "Filter" + i, () => { filter = type; galleryPage = 0; Render(); return Task.CompletedTask; }, filter == i);
                toolbar.Children.Add(button);
            }
            var search = new TextBox { Text = query, Width = 205, MinHeight = 36, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 8, 0), ToolTip = T("Dosya adı ara", "Search filename") };
            AutomationProperties.SetName(search, T("Dosya adı ara", "Search filename")); AutomationProperties.SetAutomationId(search, "SearchFiles");
            var searchBox = new Grid(); searchBox.Children.Add(search);
            var placeholder = Label(T("Dosya adı ara", "Search filename"), 12); placeholder.Foreground = Brush("Muted"); placeholder.Margin = new Thickness(9, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; placeholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed; searchBox.Children.Add(placeholder);
            search.TextChanged += (_, _) => { query = search.Text; placeholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed; galleryPage = 0; UpdateGallery(); };
            toolbar.Children.Add(searchBox);
            toolbar.Children.Add(MakeButton(listView ? T("Galeri", "Gallery") : T("Liste", "List"), "ToggleView", () => { listView = !listView; galleryPage = 0; Render(); return Task.CompletedTask; }));
            body.Children.Add(toolbar);
            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            countText = Label("", 12); countText.Foreground = Brush("Muted"); countText.Margin = new Thickness(0, 8, 16, 0); actions.Children.Add(countText);
            actions.Children.Add(MakeButton(T("Görünenleri seç", "Select visible"), "SelectVisible", () => { foreach (var item in Visible()) selectedItems.Add(item); UpdateGallery(); return Task.CompletedTask; }));
            actions.Children.Add(MakeButton(T("Seçimi temizle", "Clear selection"), "ClearSelection", () => { selectedItems.Clear(); UpdateGallery(); return Task.CompletedTask; }));
            body.Children.Add(actions);
            gallery = listView ? new StackPanel() : new WrapPanel();
            body.Children.Add(gallery);
            var paging = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            paging.Children.Add(MakeButton(T("Önceki", "Previous"), "PreviousPage", () => { galleryPage = Math.Max(0, galleryPage - 1); UpdateGallery(); return Task.CompletedTask; }));
            paging.Children.Add(MakeButton(T("Sonraki", "Next"), "NextPage", () => { if ((galleryPage + 1) * PageSize < Visible().Count) galleryPage++; UpdateGallery(); return Task.CompletedTask; }));
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
        copyButton = MakeButton(T("Seçileni kopyala", "Copy selected"), "CopySelected", CopySelected, true); bottomActions.Children.Add(copyButton); Add(bottom, bottomActions, 0, 1);
        var bottomBorder = new Border { Background = Brush("Sidebar"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        Add(UsbHost, bottomBorder, 2);
        if (items.Count > 0) UpdateGallery(); else UpdateSelection();
    }
    private List<UsbItem> Visible() => items.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) && (filter == 0 || (filter == 2) == IsVideo(x.Name))).ToList();
    private static bool IsVideo(string name) => new[] { ".mov", ".mp4", ".m4v", ".avi" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    private void UpdateGallery()
    {
        if (gallery == null) return;
        StopPreviews(); previewTiles.Clear();
        var visible = Visible();
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
            var size = Label($"{item.Length:N0} B", 12); size.Foreground = Brush("Muted"); content.Children.Add(size);
            var check = new CheckBox { Content = content, IsChecked = selectedItems.Contains(item), Width = listView ? 440 : 160, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(8), Background = Brush("Surface"), Foreground = Brush("Text"), BorderBrush = Brush("Stroke"), IsEnabled = !busy };
            check.Style = (Style)FindResource("Aktarim.MediaTile");
            AutomationProperties.SetName(check, item.Name);
            AutomationProperties.SetAutomationId(check, "File-" + index);
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
                while (true) { token.ThrowIfCancellationRequested(); int read = input.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)); if (read == 0) break; output.Write(buffer, 0, read); if (output.Length > limit) return null; }
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
        decimal bytes = selectedItems.Sum(x => (decimal)x.Length);
        selectionText.Text = $"{selectedItems.Count:N0} " + T("dosya seçildi", "files selected") + $" · {bytes / 1048576:N2} MB" + (hidden > 0 ? $" · {hidden:N0} " + T("görünüm dışında", "outside view") : "");
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
            "copying" => T("Kopyalanıyor: ", "Copying: ") + activeName + $" · {current:N0} / {total:N0} B",
            "verifying" => T("Yerel kopya doğrulanıyor: ", "Verifying local copy: ") + activeName,
            "completed" => T("Yerel kopya doğrulandı: ", "Local copy verified: ") + activeName,
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
        string destination = settings.Destination;
        selectedItems.Clear(); busy = true; Render();
        await deviceGate.WaitAsync();
        try
        {
            foreach (var item in selected)
            {
                activeName = item.Name; current = 0; total = item.Length; state = "copying"; UpdateStatus();
                var progress = new Progress<long>(value => { current = value; UpdateStatus(); });
                try
                {
                    var path = await Task.Run(async () =>
                    {
                        using var source = item.Open();
                        return await UsbCopy.CopyAsync(source, item.Length, destination, item.Name, progress: progress,
                            verifying: () => Dispatcher.Invoke(() => { state = "verifying"; UpdateStatus(); }));
                    });
                    state = "completed"; activeName = item.Name; jobs.Add((true, path));
                }
                catch (Exception) { state = "failed"; activeName = item.Name; jobs.Add((false, item.Name)); break; }
                UpdateStatus();
            }
        }
        finally { deviceGate.Release(); busy = false; Render(); }
    }
}
