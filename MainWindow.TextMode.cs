using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace Compare;

/// <summary>
/// 텍스트 비교 (직접 입력한 A·B 를 좌우로 비교)와 공유 (보이는 비교 결과를 HTML 로 만들어 File Shared 링크로).
/// 텍스트 비교는 폴더 비교와 같은 좌우 편집기를 쓰며, 모드를 오가도 입력한 글과 폴더 쪽에서 보던 파일은 그대로 돌아옴.
/// </summary>
public partial class MainWindow
{
    private bool _isTextMode;
    private bool _textIgnoreWhitespace;
    private bool _isTextStale;
    private bool _isSharing;
    private string _savedTextA = "";
    private string _savedTextB = "";
    private (string Category, string Rel, TreeViewItem Item)? _folderFileBeforeText;
    private string? _lastShareLink;

    internal bool IsTextMode => _isTextMode;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (!SwitchMode(RbModeText.IsChecked == true))
        {
            RbModeFolder.IsChecked = !_isTextMode;
            RbModeText.IsChecked = _isTextMode;
        }
    }

    /// <summary>폴더 ↔ 텍스트. 폴더 비교·복사·공유 중이거나 저장하지 않은 파일 수정을 취소하면 바꾸지 않음</summary>
    internal bool SwitchMode(bool textMode)
    {
        if (textMode == _isTextMode) return true;
        if (_comparisonCancellation != null || _isCopying || _isSharing) return false;

        if (textMode)
        {
            if (!ConfirmPendingChanges()) return false;
            _folderFileBeforeText = _currentRelativePath != null && _selectedTreeItem != null
                ? (_selectedTreeItem.Tag is (string category, string _) ? category : "", _currentRelativePath, _selectedTreeItem)
                : null;
            ResetCurrentEditor();
            _isTextMode = true;
            ApplyModeLayout();
            LoadTexts(_savedTextA, _savedTextB);
            RtbDiffA.Focus();
            return true;
        }

        _savedTextA = GetDocumentText(RtbDiffA.Document);
        _savedTextB = GetDocumentText(RtbDiffB.Document);
        _isTextMode = false;
        ResetCurrentEditor();
        ApplyModeLayout();
        var previous = _folderFileBeforeText;
        _folderFileBeforeText = null;
        if (previous is { } file && _lastResult != null)
        {
            try { LoadFileIntoEditors(file.Category, file.Rel, file.Item); }
            catch (Exception ex)
            {
                ResetCurrentEditor();
                ShowMessage("파일을 여는 중 오류가 발생했습니다.\n" + ex.Message);
            }
        }
        else
        {
            ShowMessage(_lastResult == null ? "파일을 선택하세요." : "비교가 완료되었습니다. 파일을 선택하세요.");
        }
        return true;
    }

    private void ApplyModeLayout()
    {
        bool text = _isTextMode;
        Visibility folderOnly = text ? Visibility.Collapsed : Visibility.Visible;
        Visibility textOnly = text ? Visibility.Visible : Visibility.Collapsed;
        GridFolderInputs.Visibility = folderOnly;
        BtnCompare.Visibility = BtnSave.Visibility = folderOnly;
        BtnTextCompare.Visibility = BtnTextClear.Visibility = textOnly;
        BorderFileTree.Visibility = BtnCopySelected.Visibility = folderOnly;
        BorderTextSummary.Visibility = textOnly;
        BorderCompareProgress.Visibility = !text && _lastResult != null ? Visibility.Visible : Visibility.Collapsed;
        BtnSaveFileA.Visibility = BtnSaveFileB.Visibility = folderOnly;
        PanelActions.Margin = new Thickness(0, text ? 14 : 0, 0, 10);
        TxtModeTitle.Text = text ? "텍스트 비교" : "파일 비교";
        Title = "Compare · " + TxtModeTitle.Text;
        ChkIgnoreWhitespace.IsChecked = text ? _textIgnoreWhitespace : _ignoreWhitespace;
        RbModeFolder.IsChecked = !text;
        RbModeText.IsChecked = text;
        UpdateEditorState();
    }

    /// <summary>두 텍스트를 편집기에 넣고 비교해 색을 칠함 (두 편집기 모두 고칠 수 있음)</summary>
    internal void LoadTexts(string textA, string textB)
    {
        TxtInfoMessage.Visibility = Visibility.Collapsed;
        GridDiffPanel.Visibility = Visibility.Visible;
        GridDiffPanel.UpdateLayout();
        ShowDiffRows(BuildRowsForEditors(
            FileContentComparer.NormalizeLineEndings(textA),
            FileContentComparer.NormalizeLineEndings(textB),
            _textIgnoreWhitespace));
        RtbDiffA.IsReadOnly = RtbDiffB.IsReadOnly = false;
        RtbDiffA.ToolTip = RtbDiffB.ToolTip = null;
        _isTextStale = false;
        UpdateTextSummary();
        UpdateEditorState();
    }

    /// <summary>편집기에 지금 있는 글로 다시 비교 (Ctrl+Enter). 커서는 같은 줄·칸으로 돌아감</summary>
    internal void CompareText()
    {
        if (!_isTextMode) return;
        RichTextBox? focused = RtbDiffA.IsKeyboardFocusWithin ? RtbDiffA : RtbDiffB.IsKeyboardFocusWithin ? RtbDiffB : null;
        var caret = focused != null ? CaretLineColumn(focused) : default;
        LoadTexts(GetDocumentText(RtbDiffA.Document), GetDocumentText(RtbDiffB.Document));
        if (focused != null) PlaceCaret(focused, caret.Line, caret.Column);
    }

    private void BtnTextCompare_Click(object sender, RoutedEventArgs e) => CompareText();

    private void BtnTextClear_Click(object sender, RoutedEventArgs e)
    {
        LoadTexts("", "");
        RtbDiffA.Focus();
    }

    private void OnTextEdited(RichTextBox editor, TextBlock lineNumbers)
    {
        string text = GetDocumentText(editor.Document);
        DiffDocumentBuilder.SetMinimumWidth(editor.Document, text.Split('\n'));
        lineNumbers.Text = BuildLineNumbers(text);
        CanvasScrollMarkers.Children.Clear();
        if (!_isTextStale)
        {
            _isTextStale = true;
            UpdateTextSummary();
        }
    }

    private void UpdateTextSummary()
    {
        var counts = DiffHtmlReport.Count(_currentDiffRows);
        TxtCountModified.Text = counts.Modified.ToString("N0");
        TxtCountOnlyA.Text = counts.OnlyA.ToString("N0");
        TxtCountOnlyB.Text = counts.OnlyB.ToString("N0");
        bool empty = _currentDiffRows.All(row => row.A.Length == 0 && row.B.Length == 0);
        TxtTextState.Text = _isTextStale ? "수정됨 · 비교 (Ctrl+Enter)"
            : empty ? "A·B 에 입력하거나 붙여 넣으세요"
            : counts.Identical ? "차이 없음" : "";
        TxtTextState.Visibility = TxtTextState.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static (int Line, int Column) CaretLineColumn(RichTextBox box)
    {
        string before = FileContentComparer.NormalizeLineEndings(new TextRange(box.Document.ContentStart, box.CaretPosition).Text);
        int lastBreak = before.LastIndexOf('\n');
        return (before.Count(ch => ch == '\n'), before.Length - (lastBreak + 1));
    }

    private static void PlaceCaret(RichTextBox box, int line, int column)
    {
        var paragraphs = box.Document.Blocks.OfType<Paragraph>().ToList();
        if (paragraphs.Count == 0) return;
        Paragraph paragraph = paragraphs[Math.Min(line, paragraphs.Count - 1)];
        TextPointer position = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        for (int i = 0; i < column; i++)
        {
            TextPointer? next = position.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null || next.CompareTo(paragraph.ContentEnd) > 0) break;
            position = next;
        }
        box.CaretPosition = position;
    }

    private static void PastePlainText(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(System.Windows.DataFormats.UnicodeText, true) is not string text) return;
        var plain = new System.Windows.DataObject();
        plain.SetData(System.Windows.DataFormats.UnicodeText, text);
        e.DataObject = plain;
        e.FormatToApply = System.Windows.DataFormats.UnicodeText;
    }

    // ===================== 공유 =====================

    private void UpdateShareButton()
    {
        bool hasDiff = _isTextMode || (_currentRelativePath != null && GridDiffPanel.Visibility == Visibility.Visible);
        BtnShare.IsEnabled = hasDiff && !_isSharing;
        BtnShare.ToolTip = hasDiff ? "보이는 비교 결과를 File Shared 링크로 만들기" : "파일을 열면 공유할 수 있습니다";
    }

    /// <summary>지금 보이는 비교 결과의 공유 파일 (텍스트 모드는 먼저 다시 비교, 파일은 저장하지 않은 수정도 포함)</summary>
    internal (string FileName, string Html)? CreateShareDocument()
    {
        DateTime now = DateTime.Now;
        if (_isTextMode)
        {
            CompareText();
            return (DiffHtmlReport.FileName("텍스트", now),
                DiffHtmlReport.Build(_currentDiffRows, "텍스트", "입력한 텍스트", "입력한 텍스트", now, _textIgnoreWhitespace));
        }

        if (_currentRelativePath == null || GridDiffPanel.Visibility != Visibility.Visible) return null;
        if (_isDirtyA || _isDirtyB) RefreshDiffFromEditors();
        string nameA = _fileDataA == null ? "(A 폴더에 없음)" : Path.Combine(_dirA, _currentRelativePath) + (_isDirtyA ? " · 저장하지 않은 수정 포함" : "");
        string nameB = _fileDataB == null ? "(B 폴더에 없음)" : Path.Combine(_dirB, _currentRelativePath) + (_isDirtyB ? " · 저장하지 않은 수정 포함" : "");
        return (DiffHtmlReport.FileName(Path.GetFileName(_currentRelativePath), now),
            DiffHtmlReport.Build(_currentDiffRows, _currentRelativePath, nameA, nameB, now, _ignoreWhitespace));
    }

    private async void BtnShare_Click(object sender, RoutedEventArgs e)
    {
        if (_isSharing || CreateShareDocument() is not { } share) return;
        string folder = Path.Combine(Path.GetTempPath(), "Compare", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, share.FileName);
        _isSharing = true;
        BtnShare.Content = "공유 중…";
        UpdateShareButton();
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(path, share.Html, new UTF8Encoding(false));
            ShowShareLink(await FileShared.ShareAsync(path));
        }
        catch (Exception ex) when (ex is FileSharedException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "공유", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _isSharing = false;
            BtnShare.Content = "공유";
            UpdateShareButton();
        }
    }

    private void ShowShareLink(string link)
    {
        _lastShareLink = link;
        TxtShareLink.Text = link;
        BorderShareLink.Visibility = Visibility.Visible;
        TxtShareLinkState.Text = CopyText(link) ? "공유 링크 · 복사됨" : "공유 링크";
    }

    private void BtnCopyShareLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastShareLink != null && CopyText(_lastShareLink)) TxtShareLinkState.Text = "공유 링크 · 복사됨";
    }

    private void BtnOpenShareLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastShareLink == null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastShareLink) { UseShellExecute = true }); }
        catch (Win32Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "공유"); }
    }

    private static bool CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
