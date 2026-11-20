using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Compare;

/// <summary>
/// dotnet run --project Compare.Checks -- snapshot-compare a.png
/// 임시 폴더에 만든 두 샘플 폴더를 실제로 비교하고, 다른 파일 하나를 열어 둔 화면을 그림으로 (스토어 스크린샷용).
/// 사용자의 마지막 폴더 설정은 건드리지 않음 (PathSettings.SettingsPathOverride)
/// </summary>
internal static class FolderSnapshot
{
    public static int Run(string output)
    {
        string root = Path.Combine(Path.GetTempPath(), "Compare.Checks", "sample-" + Guid.NewGuid().ToString("N")[..6]);
        string a = Path.Combine(root, "보고서 (지난주)"), b = Path.Combine(root, "보고서 (이번주)");
        Write(a, "주간 보고.txt", "1. 설비 점검 완료\n2. 자재 입고 3건\n3. 교육 일정 확인\n4. 다음 주 회의 준비\n");
        Write(b, "주간 보고.txt", "1. 설비 점검 완료\n2. 자재 입고 5건\n3. 교육 일정 확인\n4. 다음 주 회의 준비 (자료 첨부)\n5. 안전 점검 추가\n");
        Write(a, "설정\\app.config", "<settings>\n  <timeout>30</timeout>\n  <retry>3</retry>\n</settings>\n");
        Write(b, "설정\\app.config", "<settings>\n  <timeout>60</timeout>\n  <retry>3</retry>\n</settings>\n");
        Write(a, "회의록.txt", "참석: 4명\n안건: 일정 조정\n");
        Write(b, "회의록.txt", "참석: 4명\n안건: 일정 조정\n");
        Write(a, "이전 자료.txt", "지난주에만 있는 파일\n");
        Write(b, "새 자료.txt", "이번 주에 새로 만든 파일\n");
        PathSettings.SettingsPathOverride = Path.Combine(root, "paths.json");
        // 비교는 await 로 이어지므로 앱처럼 화면 스레드로 돌아오게
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false };
        window.Show();
        window.TxtDirA.Text = a;
        window.TxtDirB.Text = b;
        window.BtnCompare.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(TimeSpan.FromSeconds(4));
        foreach (var item in Items(window.TvFileList)) item.IsExpanded = true;
        Pump(TimeSpan.FromSeconds(1));
        foreach (var item in Items(window.TvFileList)) item.IsExpanded = true;
        Pump(TimeSpan.FromSeconds(1));
        var file = Items(window.TvFileList).FirstOrDefault(item => item.Tag is (string _, string rel) && rel.EndsWith("주간 보고.txt", StringComparison.Ordinal));
        if (file is null) { Console.WriteLine("no changed file in the tree"); window.Close(); return 1; }
        typeof(MainWindow).GetMethod("SelectFileNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [file, false, false]);
        Pump(TimeSpan.FromSeconds(2));

        var content = (FrameworkElement)window.Content;
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(size));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);
        Console.WriteLine("saved " + output);
        window.Close();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return 0;
    }

    private static void Write(string folder, string relative, string text)
    {
        string path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
    }

    private static IEnumerable<TreeViewItem> Items(ItemsControl parent)
    {
        foreach (object item in parent.Items)
        {
            if ((item as TreeViewItem ?? parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem) is not { } container) continue;
            yield return container;
            foreach (var child in Items(container)) yield return child;
        }
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
