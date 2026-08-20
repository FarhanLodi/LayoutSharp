using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace LayoutSharp.Models;

/// <summary>
/// One cell of a recognized table, described by its origin position and span (merged cells are
/// kept as a single cell, not expanded).
/// </summary>
public sealed record TableCell
{
    /// <summary>Zero-based row of the cell's top-left corner.</summary>
    public required int Row { get; init; }

    /// <summary>Zero-based column of the cell's top-left corner.</summary>
    public required int Column { get; init; }

    /// <summary>Number of rows the cell spans (1 for an ordinary cell).</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>Number of columns the cell spans (1 for an ordinary cell).</summary>
    public int ColumnSpan { get; init; } = 1;

    /// <summary>True when the cell is a header cell (<c>&lt;th&gt;</c>).</summary>
    public bool IsHeader { get; init; }

    /// <summary>The cell's text, or null when the recognizer recovered structure only.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// The cell's location, when the recognizer reports one. In a <see cref="LayoutDocument"/> this is
    /// in original-image pixel coordinates; an <see cref="Recognition.ITableRecognizer"/> returns
    /// it in crop-pixel coordinates and <see cref="Services.LayoutService"/> shifts it into page space.
    /// </summary>
    public LayoutBox? BoundingBox { get; init; }
}

/// <summary>
/// The structure (and, when the engine recovers it, the text) of a table region: a row/column grid
/// described by its origin cells with spans, plus the recognizer's original HTML when it produced
/// any. Produced by an <see cref="Recognition.ITableRecognizer"/> and stored on
/// <see cref="LayoutBlock.Table"/>.
/// </summary>
/// <remarks>
/// Rendering helpers never throw on odd input: cells outside <see cref="RowCount"/> ×
/// <see cref="ColumnCount"/> grow the rendered grid, overlapping cells keep the first value, and
/// spans are clamped to the table.
/// </remarks>
public sealed record TableStructure
{
    // Guards against hostile or degenerate markup: a single colspan="1000000" would otherwise
    // materialize a million columns. These bounds are far above any real recovered table.
    private const int MaxSpan = 512;
    private const int MaxRows = 4096;
    private const int MaxColumns = 1024;
    private const int MaxCells = 262_144;

    /// <summary>Number of rows in the grid.</summary>
    public required int RowCount { get; init; }

    /// <summary>Number of columns in the grid.</summary>
    public required int ColumnCount { get; init; }

    /// <summary>The origin cells, row-major. Spans are preserved, not expanded.</summary>
    public required IReadOnlyList<TableCell> Cells { get; init; }

    /// <summary>The recognizer's original table markup, kept verbatim, or null when it produced none.</summary>
    public string? Html { get; init; }

    /// <summary>A table with no rows, columns or cells.</summary>
    public static TableStructure Empty { get; } = new() { RowCount = 0, ColumnCount = 0, Cells = Array.Empty<TableCell>() };

    /// <summary>True when the table has no cells (or no rows / columns).</summary>
    [JsonIgnore]
    public bool IsEmpty => Cells.Count == 0 || RowCount <= 0 || ColumnCount <= 0;

    /// <summary>True when any cell spans more than one row or column (merged cells).</summary>
    [JsonIgnore]
    public bool HasSpans
    {
        get
        {
            foreach (var cell in Cells)
                if (cell.RowSpan > 1 || cell.ColumnSpan > 1) return true;
            return false;
        }
    }

    /// <summary>
    /// Parses the first <c>&lt;table&gt;</c> in <paramref name="html"/> into a <see cref="TableStructure"/>,
    /// keeping merged cells as single spanning cells. Understands <c>table</c>/<c>tr</c>/<c>td</c>/<c>th</c>
    /// with <c>rowspan</c>/<c>colspan</c>, decodes entities, collapses whitespace, turns <c>&lt;br&gt;</c>
    /// into a space, ignores every other tag while keeping its text, and never throws — unclosed tags,
    /// missing <c>tbody</c>, mixed casing and stray text all parse to the best available interpretation.
    /// </summary>
    /// <param name="html">Table markup, e.g. the HTML a PP-Structure / SLANet engine emits.</param>
    /// <returns>The parsed table with <see cref="Html"/> set to <paramref name="html"/>, or null when no cell was found.</returns>
    public static TableStructure? FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var rows = ScanRows(html);
        if (rows.Count == 0) return null;

        // Browser-like placement: each raw cell goes into the first free column of its row, and a
        // rowspan marks the columns below as taken so later rows flow around it.
        var occupied = new List<List<bool>>();
        var cells = new List<TableCell>();
        int width = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            EnsureRow(occupied, r);
            int column = 0;
            foreach (var raw in rows[r])
            {
                while (column < occupied[r].Count && occupied[r][column]) column++;
                if (column >= MaxColumns) break;

                int colSpan = Math.Min(raw.ColSpan, MaxColumns - column);
                int rowSpan = Math.Min(raw.RowSpan, rows.Count - r);   // clamp to the table, as browsers do
                for (int dr = 0; dr < rowSpan; dr++)
                {
                    EnsureRow(occupied, r + dr);
                    for (int dc = 0; dc < colSpan; dc++)
                        Mark(occupied[r + dr], column + dc);
                }

                cells.Add(new TableCell
                {
                    Row = r,
                    Column = column,
                    RowSpan = rowSpan,
                    ColumnSpan = colSpan,
                    IsHeader = raw.IsHeader,
                    Text = raw.Text,
                });
                column += colSpan;
                width = Math.Max(width, column);
            }
        }

        if (cells.Count == 0) return null;
        return new TableStructure { RowCount = rows.Count, ColumnCount = width, Cells = cells, Html = html };
    }

    /// <summary>
    /// Expands the cells into a rectangular grid of strings (<see cref="RowCount"/> rows, each of
    /// <see cref="ColumnCount"/> values, grown if a cell lies outside): a merged cell's text is
    /// repeated over every position it covers, positions no cell covers are empty strings, and
    /// null cell text becomes an empty string.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ToGrid()
    {
        var (rows, columns) = GridSize();
        if (rows == 0 || columns == 0) return Array.Empty<IReadOnlyList<string>>();

        var grid = new string?[rows][];
        for (int r = 0; r < rows; r++) grid[r] = new string?[columns];

        foreach (var cell in Cells)
        {
            if (cell.Row < 0 || cell.Column < 0) continue;
            int rowEnd = Math.Min(rows, cell.Row + Math.Max(1, cell.RowSpan));
            int colEnd = Math.Min(columns, cell.Column + Math.Max(1, cell.ColumnSpan));
            for (int r = cell.Row; r < rowEnd; r++)
                for (int c = cell.Column; c < colEnd; c++)
                    grid[r][c] ??= cell.Text ?? string.Empty;   // overlapping cells: first one wins
        }

        var result = new IReadOnlyList<string>[rows];
        for (int r = 0; r < rows; r++)
        {
            var line = new string[columns];
            for (int c = 0; c < columns; c++) line[c] = grid[r][c] ?? string.Empty;
            result[r] = line;
        }
        return result;
    }

    /// <summary>
    /// Renders the cells as canonical HTML: <c>&lt;table&gt;</c> with one <c>&lt;tr&gt;</c> per row and
    /// <c>&lt;td&gt;</c> / <c>&lt;th&gt;</c> cells carrying <c>rowspan</c> / <c>colspan</c> attributes,
    /// text HTML-escaped. Rows are separated by line breaks; there is no trailing newline.
    /// </summary>
    public string ToHtml()
    {
        var (rows, _) = GridSize();
        var byRow = new List<TableCell>?[rows];
        foreach (var cell in Cells)
        {
            if (cell.Row < 0 || cell.Row >= rows) continue;
            (byRow[cell.Row] ??= new List<TableCell>()).Add(cell);
        }

        var sb = new StringBuilder();
        sb.Append("<table>");
        for (int r = 0; r < rows; r++)
        {
            sb.AppendLine().Append("<tr>");
            var rowCells = byRow[r];
            if (rowCells is not null)
            {
                rowCells.Sort(static (a, b) => a.Column.CompareTo(b.Column));
                foreach (var cell in rowCells)
                {
                    var tag = cell.IsHeader ? "th" : "td";
                    sb.Append('<').Append(tag);
                    if (cell.RowSpan > 1) sb.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                    if (cell.ColumnSpan > 1) sb.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                    sb.Append('>').Append(HtmlEscape(cell.Text)).Append("</").Append(tag).Append('>');
                }
            }
            sb.Append("</tr>");
        }
        sb.AppendLine().Append("</table>");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the table as Markdown: a GitHub pipe table (first row as the header row, <c>|</c> escaped,
    /// line breaks collapsed) when no cell spans more than one row or column, otherwise the
    /// <see cref="ToHtml"/> markup, which Markdown renderers pass through. Empty tables render as an
    /// empty string.
    /// </summary>
    public string ToMarkdown()
    {
        if (IsEmpty) return string.Empty;
        if (HasSpans) return ToHtml();

        var grid = ToGrid();
        if (grid.Count == 0) return string.Empty;
        int columns = grid[0].Count;

        var sb = new StringBuilder();
        AppendPipeRow(sb, grid[0]);
        sb.AppendLine();
        sb.Append('|');
        for (int c = 0; c < columns; c++) sb.Append(" --- |");
        for (int r = 1; r < grid.Count; r++)
        {
            sb.AppendLine();
            AppendPipeRow(sb, grid[r]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders the expanded grid (<see cref="ToGrid"/>) as RFC 4180 CSV: a value is quoted when it
    /// contains the delimiter, a double quote, CR or LF, embedded quotes are doubled, and every row —
    /// including the last — is terminated with CRLF.
    /// </summary>
    /// <param name="delimiter">Field separator. Default <c>,</c>; pass <c>;</c> or <c>\t</c> as needed.</param>
    public string ToCsv(char delimiter = ',')
    {
        var sb = new StringBuilder();
        foreach (var row in ToGrid())
        {
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0) sb.Append(delimiter);
                var value = row[c];
                bool quote = value.IndexOf(delimiter) >= 0 || value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0;
                if (quote) sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                else sb.Append(value);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns a copy with every cell's <see cref="TableCell.BoundingBox"/> shifted by
    /// (<paramref name="dx"/>, <paramref name="dy"/>). Cells without a box are unchanged. Used to
    /// move crop-space boxes into page space.
    /// </summary>
    public TableStructure Offset(double dx, double dy)
    {
        if (Cells.Count == 0 || (dx == 0 && dy == 0)) return this;
        var shifted = new TableCell[Cells.Count];
        for (int i = 0; i < shifted.Length; i++)
        {
            var cell = Cells[i];
            shifted[i] = cell.BoundingBox is { } box
                ? cell with { BoundingBox = new LayoutBox(box.MinX + dx, box.MinY + dy, box.MaxX + dx, box.MaxY + dy) }
                : cell;
        }
        return this with { Cells = shifted };
    }

    // ---- rendering helpers ----

    /// <summary>Grid extent: the declared size, grown to cover any cell that lies outside it, bounded.</summary>
    private (int Rows, int Columns) GridSize()
    {
        int rows = Math.Max(0, RowCount);
        int columns = Math.Max(0, ColumnCount);
        foreach (var cell in Cells)
        {
            if (cell.Row < 0 || cell.Column < 0) continue;
            rows = Math.Max(rows, cell.Row + Math.Max(1, cell.RowSpan));
            columns = Math.Max(columns, cell.Column + Math.Max(1, cell.ColumnSpan));
        }
        return (Math.Min(rows, MaxRows), Math.Min(columns, MaxColumns));
    }

    private static void AppendPipeRow(StringBuilder sb, IReadOnlyList<string> row)
    {
        sb.Append('|');
        foreach (var value in row)
            sb.Append(' ').Append(PipeCell(value)).Append(" |");
    }

    /// <summary>One line, with the pipe (the only character GFM cannot take literally in a cell) escaped.</summary>
    private static string PipeCell(string value)
    {
        if (value.Length == 0) return value;
        var oneLine = value.IndexOfAny(new[] { '\r', '\n' }) >= 0
            ? string.Join(' ', value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : value.Trim();
        return oneLine.Replace("|", "\\|");
    }

    private static string HtmlEscape(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---- HTML scanner (ported from EasyOcrSharp's TableHtmlParser; same author) ----

    /// <summary>A cell as it appeared in the markup.</summary>
    private readonly record struct RawCell(string Text, int ColSpan, int RowSpan, bool IsHeader);

    /// <summary>
    /// Walks the markup once, emitting one list of <see cref="RawCell"/> per <c>&lt;tr&gt;</c>.
    /// Everything that is not a row or cell boundary is treated as cell text.
    /// </summary>
    private static List<List<RawCell>> ScanRows(string html)
    {
        var rows = new List<List<RawCell>>();
        List<RawCell>? currentRow = null;
        var cell = new StringBuilder();
        bool inCell = false;
        bool cellIsHeader = false;
        int colSpan = 1;
        int rowSpan = 1;
        int cellCount = 0;

        void CommitCell()
        {
            if (!inCell) return;
            currentRow ??= new List<RawCell>();
            if (cellCount < MaxCells)
            {
                currentRow.Add(new RawCell(Clean(cell.ToString()), colSpan, rowSpan, cellIsHeader));
                cellCount++;
            }
            cell.Clear();
            inCell = false;
            colSpan = 1;
            rowSpan = 1;
        }

        void CommitRow()
        {
            CommitCell();
            if (currentRow is { Count: > 0 } && rows.Count < MaxRows)
                rows.Add(currentRow);
            currentRow = null;
        }

        int i = 0;
        while (i < html.Length)
        {
            char ch = html[i];
            if (ch != '<')
            {
                if (inCell) cell.Append(ch);
                i++;
                continue;
            }

            // An unterminated '<' is literal text, not a tag.
            int end = FindTagEnd(html, i);
            if (end < 0)
            {
                if (inCell) cell.Append(html.AsSpan(i));
                break;
            }

            var tag = html.AsSpan(i + 1, end - i - 1);
            i = end + 1;

            if (tag.StartsWith("!--", StringComparison.Ordinal)) continue;          // comment
            if (tag.Length == 0 || tag[0] == '!' || tag[0] == '?') continue;         // doctype / PI

            bool closing = tag[0] == '/';
            var nameSpan = closing ? tag[1..] : tag;
            int nameLength = 0;
            while (nameLength < nameSpan.Length && !char.IsWhiteSpace(nameSpan[nameLength]) && nameSpan[nameLength] != '/')
                nameLength++;
            var name = nameSpan[..nameLength];

            if (name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                // Both <tr> and </tr> close the row in progress; an unclosed row is common in
                // generated markup and must not swallow the next one.
                CommitRow();
                if (!closing) currentRow = new List<RawCell>();
            }
            else if (name.Equals("td", StringComparison.OrdinalIgnoreCase) || name.Equals("th", StringComparison.OrdinalIgnoreCase))
            {
                CommitCell();
                if (!closing)
                {
                    inCell = true;
                    cellIsHeader = name.Equals("th", StringComparison.OrdinalIgnoreCase);
                    colSpan = ReadSpan(nameSpan[nameLength..], "colspan");
                    rowSpan = ReadSpan(nameSpan[nameLength..], "rowspan");
                    currentRow ??= new List<RawCell>();
                }
            }
            else if (name.Equals("table", StringComparison.OrdinalIgnoreCase) && closing)
            {
                CommitRow();
                break;      // only the first table in the fragment
            }
            else if (name.Equals("br", StringComparison.OrdinalIgnoreCase) && inCell)
            {
                cell.Append(' ');   // cells are single-line values here
            }

            // Every other tag (<b>, <span>, <p>, <tbody>, ...) is skipped while its text is kept.
        }

        CommitRow();
        return rows;
    }

    /// <summary>
    /// Finds the index of the '&gt;' closing the tag starting at <paramref name="start"/>, honouring
    /// quoted attribute values and running to <c>--&gt;</c> for comments. -1 when never terminated.
    /// </summary>
    private static int FindTagEnd(string html, int start)
    {
        if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
        {
            int close = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            return close < 0 ? -1 : close + 2;
        }

        char quote = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char ch = html[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Reads a <c>colspan</c>/<c>rowspan</c> attribute (double-, single- or un-quoted). Missing,
    /// unparsable or out-of-range values fall back to 1; huge values are clamped rather than trusted.
    /// </summary>
    private static int ReadSpan(ReadOnlySpan<char> attributes, string attributeName)
    {
        int at = attributes.IndexOf(attributeName, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 1;

        var rest = attributes[(at + attributeName.Length)..];
        int eq = rest.IndexOf('=');
        if (eq < 0) return 1;

        rest = rest[(eq + 1)..].TrimStart();
        if (rest.Length == 0) return 1;

        if (rest[0] is '"' or '\'')
        {
            char quote = rest[0];
            rest = rest[1..];
            int close = rest.IndexOf(quote);
            if (close >= 0) rest = rest[..close];
        }
        else
        {
            int stop = 0;
            while (stop < rest.Length && !char.IsWhiteSpace(rest[stop]) && rest[stop] != '/') stop++;
            rest = rest[..stop];
        }

        return int.TryParse(rest.Trim(), out int value) && value >= 1 ? Math.Min(value, MaxSpan) : 1;
    }

    private static void EnsureRow(List<List<bool>> occupied, int index)
    {
        while (occupied.Count <= index && occupied.Count < MaxRows)
            occupied.Add(new List<bool>());
    }

    private static void Mark(List<bool> row, int column)
    {
        while (row.Count <= column) row.Add(false);
        row[column] = true;
    }

    /// <summary>
    /// Turns raw cell content into a value: entities decoded, whitespace runs (including the
    /// non-breaking space <c>&amp;nbsp;</c> decodes to) collapsed to single spaces, then trimmed.
    /// </summary>
    private static string Clean(string raw)
    {
        if (raw.Length == 0) return string.Empty;

        // WebUtility handles the full named-entity set plus &#NNN;/&#xHH; and leaves unknown
        // ampersands alone, which is exactly the forgiving behaviour wanted here.
        string decoded = raw.Contains('&', StringComparison.Ordinal) ? WebUtility.HtmlDecode(raw) : raw;

        var builder = new StringBuilder(decoded.Length);
        bool pendingSpace = false;
        foreach (char ch in decoded)
        {
            if (char.IsWhiteSpace(ch) || ch == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
