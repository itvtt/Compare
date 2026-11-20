using System.IO;
using System.Net;
using System.Text;

namespace Compare;

/// <summary>
/// 화면의 비교 결과(DiffRow)를 HTML 파일 하나로 만듦: 공유 링크로 보낼 때 받는 사람이 브라우저에서 바로 봄.
/// 좌우 표, 줄 번호, 줄 색(바뀜·A에만·B에만), 글자 단위 강조. 바뀐 곳에서 먼 같은 줄은 접어 둠(눌러서 펼침).
/// </summary>
public static class DiffHtmlReport
{
    /// <summary>바뀐 줄 앞뒤로 접지 않고 보여 줄 같은 줄 수</summary>
    public const int ContextLines = 3;

    public readonly record struct Counts(int Modified, int OnlyA, int OnlyB)
    {
        public bool Identical => Modified == 0 && OnlyA == 0 && OnlyB == 0;
    }

    public static Counts Count(IEnumerable<MainWindow.DiffRow> rows)
    {
        int modified = 0, onlyA = 0, onlyB = 0;
        foreach (var row in rows)
        {
            switch (row.Type)
            {
                case DiffType.Modified: modified++; break;
                case DiffType.Removed: onlyA++; break;
                case DiffType.Added: onlyB++; break;
            }
        }
        return new Counts(modified, onlyA, onlyB);
    }

    /// <summary>공유 파일 이름: "&lt;제목&gt; 비교 2026-11-18 1530.html" (파일 이름에 못 쓰는 글자는 _)</summary>
    public static string FileName(string title, DateTime created)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string(title.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        if (safe.Length == 0) safe = "비교";
        if (safe.Length > 60) safe = safe[..60];
        return $"{safe} 비교 {created:yyyy-MM-dd HHmm}.html";
    }

    public static string Build(IReadOnlyList<MainWindow.DiffRow> rows, string title, string nameA, string nameB,
        DateTime created, bool ignoreWhitespace)
    {
        var counts = Count(rows);
        var html = new StringBuilder(4096 + rows.Count * 160);
        html.Append("<!DOCTYPE html>\n<html lang=\"ko\"><head><meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Encode(title)).Append(" 비교</title>\n<style>\n").Append(Style).Append("</style></head>\n<body>\n");

        html.Append("<header><h1>").Append(Encode(title)).Append(" 비교</h1>\n<div class=\"meta\">")
            .Append("<span class=\"side a\">A</span> ").Append(Encode(nameA))
            .Append("<br><span class=\"side b\">B</span> ").Append(Encode(nameB))
            .Append("<br>").Append(Encode(created.ToString("yyyy-MM-dd HH:mm")))
            .Append(ignoreWhitespace ? " · 공백·줄바꿈 무시" : "").Append("</div>\n<div class=\"counts\">");
        if (counts.Identical)
            html.Append("<span class=\"chip same\">차이 없음</span>");
        else
            html.Append("<span class=\"chip mod\">바뀜 ").Append(counts.Modified).Append("</span>")
                .Append("<span class=\"chip del\">A에만 ").Append(counts.OnlyA).Append("</span>")
                .Append("<span class=\"chip add\">B에만 ").Append(counts.OnlyB).Append("</span>");
        html.Append("</div></header>\n<main><table>\n<colgroup><col class=\"ln\"><col><col class=\"ln\"><col></colgroup>\n");

        bool[] visible = VisibleRows(rows);
        int fold = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!visible[i])
            {
                int end = i;
                while (end < rows.Count && !visible[end]) end++;
                fold++;
                html.Append("<tr class=\"fold\" onclick=\"unfold(").Append(fold).Append(", this)\"><td colspan=\"4\">같은 줄 ")
                    .Append(end - i).Append("개 · 펼치기</td></tr>\n");
                for (int j = i; j < end; j++) AppendRow(html, rows[j], fold);
                i = end - 1;
                continue;
            }
            AppendRow(html, rows[i], 0);
        }

        html.Append("</table></main>\n<script>function unfold(n,row){document.querySelectorAll('tr[data-fold=\"'+n+'\"]').forEach(function(r){r.hidden=false;});row.remove();}</script>\n")
            .Append("</body></html>\n");
        return html.ToString();
    }

    /// <summary>바뀐 줄과 그 앞뒤 ContextLines 줄만 처음부터 보임. 접을 같은 줄이 ContextLines 이하면 그냥 보임</summary>
    private static bool[] VisibleRows(IReadOnlyList<MainWindow.DiffRow> rows)
    {
        var visible = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type == DiffType.Same && rows[i].BackgroundTypeA is null or DiffType.Same && rows[i].BackgroundTypeB is null or DiffType.Same)
                continue;
            for (int j = Math.Max(0, i - ContextLines); j <= Math.Min(rows.Count - 1, i + ContextLines); j++) visible[j] = true;
        }
        for (int i = 0; i < rows.Count;)
        {
            if (visible[i]) { i++; continue; }
            int end = i;
            while (end < rows.Count && !visible[end]) end++;
            if (end - i <= ContextLines) for (int j = i; j < end; j++) visible[j] = true;
            i = end;
        }
        return visible;
    }

    private static void AppendRow(StringBuilder html, MainWindow.DiffRow row, int fold)
    {
        html.Append(fold > 0 ? $"<tr data-fold=\"{fold}\" hidden>" : "<tr>");
        AppendSide(html, row.LineNoA, row.DisplayPiecesA, row.BackgroundTypeA ?? row.Type);
        AppendSide(html, row.LineNoB, row.DisplayPiecesB, row.BackgroundTypeB ?? row.Type);
        html.Append("</tr>\n");
    }

    private static void AppendSide(StringBuilder html, int? lineNo, List<MainWindow.CharPiece> pieces, DiffType type)
    {
        if (lineNo is null)
        {
            html.Append("<td class=\"ln none\"></td><td class=\"none\"></td>");
            return;
        }
        string kind = type switch { DiffType.Added => " add", DiffType.Removed => " del", DiffType.Modified => " mod", _ => "" };
        html.Append("<td class=\"ln").Append(kind).Append("\">").Append(lineNo.Value).Append("</td><td class=\"code").Append(kind).Append("\">");
        foreach (var piece in pieces)
        {
            if (piece.Text.Length == 0) continue;
            string? mark = piece.Kind switch { "removed" => "x-del", "added" => "x-add", _ => null };
            if (mark != null) html.Append("<span class=\"").Append(mark).Append("\">");
            html.Append(Encode(piece.Text));
            if (mark != null) html.Append("</span>");
        }
        html.Append("</td>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private const string Style = """
        :root { color-scheme: dark; --bg:#0B0F15; --panel:#11151C; --line:#2A3442; --text:#EEF3FA; --muted:#92A3BA;
          --a:#3875DE; --b:#B69AFF; --mod:#3B3219; --modx:#6B5A22; --del:#3F2029; --delx:#833242; --add:#133529; --addx:#216548; }
        @media (prefers-color-scheme: light) { :root { color-scheme: light; --bg:#F6F8FB; --panel:#FFFFFF; --line:#D5DCE6; --text:#16202C;
          --muted:#5B6B80; --mod:#FFF4CC; --modx:#F2D675; --del:#FDE4E7; --delx:#F5A3AE; --add:#DDF6E8; --addx:#8FDDB3; } }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font-family:"Segoe UI", "Malgun Gothic", sans-serif; }
        header { padding:18px 20px 12px; border-bottom:1px solid var(--line); background:var(--panel); position:sticky; top:0; z-index:1; }
        h1 { margin:0 0 8px; font-size:18px; font-weight:600; }
        .meta { color:var(--muted); font-size:12.5px; line-height:1.7; word-break:break-all; }
        .side { display:inline-block; width:18px; text-align:center; border-radius:4px; font-weight:700; font-size:11px; color:#fff; }
        .side.a { background:var(--a); } .side.b { background:var(--b); color:#1A1330; }
        .counts { margin-top:10px; display:flex; gap:8px; flex-wrap:wrap; }
        .chip { padding:3px 10px; border-radius:6px; font-size:12.5px; font-weight:600; border:1px solid var(--line); }
        .chip.mod { background:var(--mod); } .chip.del { background:var(--del); } .chip.add { background:var(--add); } .chip.same { background:var(--panel); }
        main { padding:12px 20px 40px; }
        table { width:100%; border-collapse:collapse; table-layout:fixed; font-family:"Cascadia Mono", Consolas, monospace; font-size:13px; background:var(--panel); border:1px solid var(--line); }
        col.ln { width:52px; }
        td { padding:1px 8px; vertical-align:top; line-height:20px; }
        td.code { white-space:pre-wrap; overflow-wrap:anywhere; tab-size:4; border-right:1px solid var(--line); }
        td.ln { color:var(--muted); text-align:right; user-select:none; border-right:1px solid var(--line); }
        td.none { background:repeating-linear-gradient(135deg, transparent 0 6px, rgba(128,128,128,.08) 6px 12px); }
        .mod { background:var(--mod); } .del { background:var(--del); } .add { background:var(--add); }
        .x-del { background:var(--delx); border-radius:2px; } .x-add { background:var(--addx); border-radius:2px; }
        tr.fold td { text-align:center; color:var(--muted); cursor:pointer; padding:4px; font-family:"Segoe UI", sans-serif; font-size:12px;
          background:rgba(56,117,222,.08); border-top:1px solid var(--line); border-bottom:1px solid var(--line); }
        tr.fold:hover td { color:var(--text); }

        """;
}
