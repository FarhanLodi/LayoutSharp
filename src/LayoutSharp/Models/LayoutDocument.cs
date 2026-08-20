using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LayoutSharp.Models;

/// <summary>
/// A structured document: an ordered collection of analyzed pages. This is the central output
/// of layout analysis — an image reduced to a typed, reading-ordered block graph.
/// </summary>
public sealed record LayoutDocument
{
    /// <summary>The document's pages, in order.</summary>
    public required IReadOnlyList<LayoutPage> Pages { get; init; }

    /// <summary>Serializes the document to indented JSON with string-named enum values.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, LayoutJsonContext.Default.LayoutDocument);

    /// <summary>Deserializes a document previously produced by <see cref="ToJson"/>.</summary>
    public static LayoutDocument? FromJson(string json)
        => JsonSerializer.Deserialize(json, LayoutJsonContext.Default.LayoutDocument);

    /// <summary>
    /// Renders the document's text-bearing blocks to plain text in reading order, one block per
    /// paragraph, with a blank line between pages. Recognized tables (<see cref="LayoutBlock.Table"/>)
    /// are emitted as tab-separated rows and recognized formulas as their <see cref="LayoutBlock.Latex"/>;
    /// blocks without any of these (figures, regions analyzed without a recognizer) are skipped.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();
        bool firstPage = true;
        foreach (var page in Pages)
        {
            if (!firstPage) sb.AppendLine();
            firstPage = false;
            foreach (var block in page.Blocks)
            {
                var text = PlainText(block);
                if (string.IsNullOrWhiteSpace(text)) continue;
                sb.AppendLine(text);
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders the document as Markdown in reading order: titles become <c>#</c> headings, section
    /// headers <c>##</c>, captions italics, list blocks bullet lists; recognized tables become pipe
    /// tables (or HTML when cells are merged) and recognized formulas <c>$$…$$</c> blocks; figures,
    /// tables and formulas without recognized content become bracketed placeholders; page headers,
    /// footers, page numbers and seals are omitted. Pages are separated by a horizontal rule.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        bool firstPage = true;
        foreach (var page in Pages)
        {
            if (!firstPage) sb.AppendLine().AppendLine("---").AppendLine();
            firstPage = false;

            bool firstBlock = true;
            foreach (var block in page.Blocks)
            {
                var rendered = RenderMarkdown(block);
                if (rendered is null) continue;
                if (!firstBlock) sb.AppendLine();
                firstBlock = false;
                sb.AppendLine(rendered);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string? RenderMarkdown(LayoutBlock block)
    {
        var text = string.IsNullOrWhiteSpace(block.Text) ? null : block.Text.Trim();
        switch (block.Type)
        {
            case LayoutBlockType.Title:
                return text is null ? null : "# " + Inline(text);
            case LayoutBlockType.SectionHeader:
                return text is null ? null : "## " + Inline(text);
            case LayoutBlockType.Caption:
                return text is null ? null : "*" + Inline(text) + "*";
            case LayoutBlockType.List:
                return text is null ? null : Bullets(text);
            case LayoutBlockType.Text:
            case LayoutBlockType.Footnote:
            case LayoutBlockType.KeyValueRegion:
            case LayoutBlockType.Other:
                return text;
            case LayoutBlockType.Figure:
                return "*[Figure]*";
            case LayoutBlockType.Table:
                return TableMarkdown(block) ?? text ?? "*[Table]*";
            case LayoutBlockType.Formula:
                return FormulaMarkdown(block) ?? text ?? "*[Formula]*";
            case LayoutBlockType.PageHeader:
            case LayoutBlockType.PageFooter:
            case LayoutBlockType.PageNumber:
            case LayoutBlockType.Seal:
            case LayoutBlockType.Checkbox:
            case LayoutBlockType.Form:
            default:
                return null;
        }
    }

    /// <summary>Collapses OCR line breaks so a heading stays on one line.</summary>
    private static string Inline(string text)
        => string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Bullets(string text)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var body = line.TrimStart('-', '*', '•', '·', ' ');
            sb.Append("- ").AppendLine(body.Length > 0 ? body : line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The block's plain-text payload: a recognized table as tab-separated rows, a recognized formula
    /// as its LaTeX, otherwise the OCR'd text.
    /// </summary>
    private static string? PlainText(LayoutBlock block)
    {
        if (block.Table is { IsEmpty: false } table)
        {
            var sb = new StringBuilder();
            foreach (var row in table.ToGrid())
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(string.Join('\t', row));
            }
            return sb.ToString();
        }
        if (!string.IsNullOrWhiteSpace(block.Latex)) return block.Latex.Trim();
        return block.Text;
    }

    /// <summary>Recognized table as Markdown (pipe table, or HTML when cells are merged), or null when there is none.</summary>
    private static string? TableMarkdown(LayoutBlock block)
    {
        if (block.Table is not { IsEmpty: false } table) return null;
        var md = table.ToMarkdown();
        return md.Length == 0 ? null : md;
    }

    /// <summary>Recognized formula as a <c>$$…$$</c> display-math block, or null when there is none.</summary>
    private static string? FormulaMarkdown(LayoutBlock block)
    {
        if (string.IsNullOrWhiteSpace(block.Latex)) return null;
        return "$$" + Environment.NewLine + block.Latex.Trim() + Environment.NewLine + "$$";
    }
}

/// <summary>
/// Source-generated JSON contract for <see cref="LayoutDocument"/>, keeping serialization
/// trim- and AOT-safe (no reflection-based fallback).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LayoutDocument))]
internal sealed partial class LayoutJsonContext : JsonSerializerContext;
