using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Compare;

/// <summary>
/// dotnet run --project Compare.Checks                      비교 결과 HTML · 텍스트 비교 확인
/// dotnet run --project Compare.Checks -- snapshot a.png    텍스트 비교 화면을 그림으로 (화면 확인용)
/// dotnet run --project Compare.Checks -- live-share        이 PC 의 File Shared(포트 5000)에 올리고 링크로 내려받아 같은지 확인
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["snapshot", var output]) return Snapshot(output, textMode: true);
            if (args is ["snapshot-folder", var folderOutput]) return Snapshot(folderOutput, textMode: false);
            if (args is ["snapshot-compare", var compareOutput]) return FolderSnapshot.Run(compareOutput);
            if (args is ["html", var htmlOutput]) return WriteSampleHtml(htmlOutput);
            if (args is ["live-share"]) return LiveShare().GetAwaiter().GetResult();
            HtmlReportChecks();
            TextModeChecks();
            Console.WriteLine("ALL PASS");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }

    private static void HtmlReportChecks()
    {
        var rows = new List<MainWindow.DiffRow>();
        for (int i = 1; i <= 10; i++) rows.Add(Same(i));
        rows.Add(new MainWindow.DiffRow
        {
            LineNoA = 11, LineNoB = 11, A = "x <script>&", B = "y <script>&", Type = DiffType.Modified,
            CharDiffA = [new MainWindow.CharPiece { Text = "x", Kind = "removed" }, new MainWindow.CharPiece { Text = " <script>&" }],
            CharDiffB = [new MainWindow.CharPiece { Text = "y", Kind = "added" }, new MainWindow.CharPiece { Text = " <script>&" }]
        });
        rows.Add(new MainWindow.DiffRow { LineNoA = 12, A = "only a", Type = DiffType.Removed });
        rows.Add(new MainWindow.DiffRow { LineNoB = 12, B = "only b", Type = DiffType.Added });
        for (int i = 13; i <= 20; i++) rows.Add(Same(i));

        var created = new DateTime(2026, 11, 18, 15, 30, 0);
        string html = DiffHtmlReport.Build(rows, "a<b>.txt", "A 경로", "B 경로", created, ignoreWhitespace: false);
        Assert(!html.Contains("<script>&"), "text must be HTML-escaped");
        Assert(html.Contains("&lt;script&gt;&amp;") && html.Contains("<title>a&lt;b&gt;.txt 비교</title>"), "escaped text and title");
        Assert(html.Contains("바뀜 1") && html.Contains("A에만 1") && html.Contains("B에만 1"), "counts");
        Assert(html.Contains("<span class=\"x-del\">x</span>") && html.Contains("<span class=\"x-add\">y</span>"), "character highlights");
        Assert(html.Contains("같은 줄 7개") && html.Contains("같은 줄 5개"), "far same lines are folded (7 before, 5 after)");
        Assert(Regex.Matches(html, "<tr data-fold=").Count == 12, "folded rows stay in the file");
        Assert(html.Contains("<td class=\"ln none\"></td>"), "missing side is an empty cell");
        Assert(DiffHtmlReport.FileName("a:b?.txt", created) == "a_b_.txt 비교 2026-11-18 1530.html", "file name: " + DiffHtmlReport.FileName("a:b?.txt", created));

        string same = DiffHtmlReport.Build([Same(1), Same(2)], "같음", "A", "B", created, ignoreWhitespace: true);
        Assert(same.Contains("차이 없음") && same.Contains("공백·줄바꿈 무시"), "identical report");
        Console.WriteLine("PASS: the shared HTML escapes text, counts changes, highlights characters and folds far same lines.");
    }

    private static void TextModeChecks()
    {
        var window = new MainWindow();
        Assert(window.SwitchMode(true) && window.IsTextMode, "switch to text mode");
        Assert(!window.RtbDiffA.IsReadOnly && !window.RtbDiffB.IsReadOnly, "both sides editable");
        Assert(window.GridFolderInputs.Visibility == Visibility.Collapsed && window.BorderTextSummary.Visibility == Visibility.Visible, "text layout");
        Assert(window.BtnShare.IsEnabled, "share is available in text mode");
        Assert(window.TxtTextState.Text.Contains("붙여 넣"), "empty hint: " + window.TxtTextState.Text);

        window.LoadTexts("one\ntwo\nthree", "one\nTWO\nthree\nfour");
        AssertCounts(window, 1, 0, 1);
        Assert(Text(window.RtbDiffB) == "one\nTWO\nthree\nfour", "B text kept");

        var share = window.CreateShareDocument();
        Assert(share is { } doc && doc.FileName.StartsWith("텍스트 비교 ", StringComparison.Ordinal) && doc.Html.Contains("TWO") && doc.Html.Contains("B에만 1"),
            "share document from the text comparison");

        // 편집하면 "수정됨", 다시 비교하면 새 결과
        new TextRange(window.RtbDiffB.Document.ContentStart, window.RtbDiffB.Document.ContentEnd).Text = "one\r\ntwo\r\nthree";
        Assert(window.TxtTextState.Text.StartsWith("수정됨", StringComparison.Ordinal), "edit marks the result stale: " + window.TxtTextState.Text);
        window.CompareText();
        AssertCounts(window, 0, 0, 0);
        Assert(window.TxtTextState.Text == "차이 없음", "identical: " + window.TxtTextState.Text);

        // 공백 무시는 텍스트 비교에만 (폴더 쪽 설정은 그대로)
        window.LoadTexts("a b\nc", "ab\nc");
        AssertCounts(window, 1, 0, 0);
        window.ChkIgnoreWhitespace.IsChecked = true;
        window.ChkIgnoreWhitespace.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertCounts(window, 0, 0, 0);

        // 모드를 오가도 입력한 글은 남고, 폴더 쪽 공백 설정은 꺼진 그대로
        Assert(window.SwitchMode(false) && !window.IsTextMode, "back to folder mode");
        Assert(window.ChkIgnoreWhitespace.IsChecked == false && window.GridFolderInputs.Visibility == Visibility.Visible, "folder layout and option restored");
        Assert(!window.BtnShare.IsEnabled, "nothing to share in folder mode without an open file");
        Assert(window.SwitchMode(true), "text mode again");
        Assert(Text(window.RtbDiffA) == "a b\nc" && window.ChkIgnoreWhitespace.IsChecked == true, "text and option kept");

        window.BtnTextClear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(Text(window.RtbDiffA) == "" && Text(window.RtbDiffB) == "", "clear");
        Console.WriteLine("PASS: text mode compares typed text, marks edits, keeps its own whitespace option and survives mode switches.");
    }

    private const string SampleA =
        "using System;\n\nclass Greeter\n{\n    public string Name { get; set; } = \"world\";\n    public void Hello() => Console.WriteLine($\"Hello, {Name}!\");\n    public void Bye() { }\n}\n// 끝";
    private const string SampleB =
        "using System;\n\nclass Greeter\n{\n    public string Name { get; set; } = \"team\";\n    public void Hello() => Console.WriteLine($\"Hi, {Name}!\");\n}\n// 끝\n// 새 줄";

    private static int WriteSampleHtml(string output)
    {
        var window = new MainWindow();
        window.SwitchMode(true);
        string a = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"// 앞 {i}")) + "\n" + SampleA;
        string b = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"// 앞 {i}")) + "\n" + SampleB;
        window.LoadTexts(a, b);
        File.WriteAllText(output, window.CreateShareDocument()!.Value.Html, new System.Text.UTF8Encoding(false));
        Console.WriteLine("saved " + output);
        return 0;
    }

    private static int Snapshot(string output, bool textMode)
    {
        var window = new MainWindow();
        if (textMode)
        {
            window.SwitchMode(true);
            window.LoadTexts(SampleA, SampleB);
        }
        window.TxtShareLink.Text = "http://<이 PC>:5000/s/1a2b3c4d";
        window.BorderShareLink.Visibility = Visibility.Visible;
        var content = (FrameworkElement)window.Content;
        var size = new System.Windows.Size(window.Width, window.Height);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
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
        return 0;
    }

    private static async Task<int> LiveShare()
    {
        var window = new MainWindow();
        window.SwitchMode(true);
        window.LoadTexts("첫 줄\n같은 줄\n<b>굵게</b>", "첫 줄!\n같은 줄\n새 줄");
        var share = window.CreateShareDocument() ?? throw new Exception("no share document");
        string folder = Path.Combine(Path.GetTempPath(), "Compare.Checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, share.FileName);
        await File.WriteAllTextAsync(path, share.Html, new System.Text.UTF8Encoding(false));
        try
        {
            string link = await FileShared.ShareAsync(path);
            Console.WriteLine("link: " + link);
            using var http = new HttpClient();
            string local = Regex.Replace(link, @"^http://[^/]+", "http://127.0.0.1:5000");
            string page = await http.GetStringAsync(local);
            string download = Regex.Match(page, "href=\"(/download/[^\"]+)\"").Groups[1].Value;
            Assert(download.Length > 0, "share page has a download link");
            string downloaded = await http.GetStringAsync("http://127.0.0.1:5000" + download);
            Assert(downloaded == share.Html, "downloaded file equals the shared HTML");
            Console.WriteLine("PASS: the comparison HTML goes up to File Shared and comes back unchanged through its link.");
            return 0;
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static MainWindow.DiffRow Same(int line) =>
        new() { LineNoA = line, LineNoB = line, A = $"same {line}", B = $"same {line}", Type = DiffType.Same };

    private static string Text(System.Windows.Controls.RichTextBox box)
    {
        string text = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Replace("\r\n", "\n");
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static void AssertCounts(MainWindow window, int modified, int onlyA, int onlyB) =>
        Assert(window.TxtCountModified.Text == modified.ToString() && window.TxtCountOnlyA.Text == onlyA.ToString() && window.TxtCountOnlyB.Text == onlyB.ToString(),
            $"counts {window.TxtCountModified.Text}/{window.TxtCountOnlyA.Text}/{window.TxtCountOnlyB.Text}, expected {modified}/{onlyA}/{onlyB}");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
