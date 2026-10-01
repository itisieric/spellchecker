using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using LLMAutocorrect.Configuration;
using LLMAutocorrect.Correction;

namespace LLMAutocorrect.Memory;

public sealed partial class SpellingHistory
{
    private const int MaximumCorrectWords = 2000;
    private const int MaximumVariantsPerWord = 50;
    private readonly SettingsManager _settings;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private HistoryStore _store = new([]);

    public string StorePath { get; }
    public string WorkbookPath { get; }
    public int CorrectWordCount { get { lock (_gate) return _store.Words.Count; } }

    public SpellingHistory(SettingsManager settings, string? storePath = null, string? workbookPath = null)
    {
        _settings = settings;
        StorePath = storePath ?? Path.Combine(settings.DataDirectory, "spelling-history.json");
        WorkbookPath = workbookPath ?? Path.Combine(settings.DataDirectory, "spelling-history.xlsx");
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(StorePath))
        {
            try
            {
                await using var stream = File.OpenRead(StorePath);
                var loaded = await JsonSerializer.DeserializeAsync<HistoryStore>(stream, cancellationToken: cancellationToken);
                if (loaded is not null)
                    lock (_gate) _store = new(loaded.Words.Take(MaximumCorrectWords).ToList());
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        await ExportWorkbookAsync(cancellationToken);
    }

    public async Task RecordCorrectionAsync(string original, string corrected, IReadOnlyList<string> protectedTokens,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.SpellingHistoryEnabled) return;
        var pairs = ExtractPairs(original, corrected, protectedTokens);
        if (pairs.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var pair in pairs)
            {
                var correct = NormalizeCorrectWord(pair.Correct);
                var entry = _store.Words.FirstOrDefault(x =>
                    string.Equals(x.CorrectWord, correct, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    entry = new CorrectWordEntry(correct, 0, now, []);
                    _store.Words.Add(entry);
                }
                entry.TotalAttempts++;
                entry.LastAttemptedUtc = now;
                var variant = entry.Variants.FirstOrDefault(x =>
                    string.Equals(x.Misspelling, pair.Misspelled, StringComparison.OrdinalIgnoreCase));
                if (variant is null && entry.Variants.Count < MaximumVariantsPerWord)
                    entry.Variants.Add(new(pair.Misspelled.ToLowerInvariant(), 1));
                else if (variant is not null)
                    variant.Count++;
            }
            _store = new(_store.Words.OrderByDescending(x => x.TotalAttempts)
                .ThenByDescending(x => x.LastAttemptedUtc).Take(MaximumCorrectWords).ToList());
        }
        await SaveAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) _store = new([]);
        await SaveAsync(cancellationToken);
    }

    internal static IReadOnlyList<SpellingPair> ExtractPairs(string original, string corrected,
        IReadOnlyList<string> protectedTokens)
    {
        var sourceWords = Words().Matches(original).Select(x => x.Value).ToArray();
        var correctedWords = Words().Matches(corrected).Select(x => x.Value).ToArray();
        if (sourceWords.Length != correctedWords.Length) return [];
        var pairs = new List<SpellingPair>();
        for (var i = 0; i < sourceWords.Length; i++)
        {
            var source = sourceWords[i];
            var replacement = correctedWords[i];
            if (source.Length < 2 || replacement.Length < 2 ||
                string.Equals(source, replacement, StringComparison.OrdinalIgnoreCase) ||
                protectedTokens.Contains(source, StringComparer.Ordinal) ||
                CorrectionValidator.Levenshtein(source.ToLowerInvariant(), replacement.ToLowerInvariant()) >
                    Math.Max(2, source.Length / 2)) continue;
            pairs.Add(new(source, replacement));
        }
        return pairs;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        HistoryStore snapshot;
        lock (_gate) snapshot = new(_store.Words.Select(x => x.Copy()).ToList());
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var jsonTemp = StorePath + ".tmp";
            await using (var stream = File.Create(jsonTemp))
                await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            File.Move(jsonTemp, StorePath, true);
            await WriteWorkbookAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _saveGate.Release(); }
    }

    private async Task ExportWorkbookAsync(CancellationToken cancellationToken)
    {
        HistoryStore snapshot;
        lock (_gate) snapshot = new(_store.Words.Select(x => x.Copy()).ToList());
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkbookPath)!);
            await WriteWorkbookAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _saveGate.Release(); }
    }

    private async Task WriteWorkbookAsync(HistoryStore snapshot, CancellationToken cancellationToken)
    {
        var temp = WorkbookPath + ".tmp";
        await Task.Run(() => SpellingWorkbookWriter.Write(temp, snapshot.Words), cancellationToken);
        File.Move(temp, WorkbookPath, true);
    }

    private static string NormalizeCorrectWord(string value) =>
        value.Length > 1 && value.All(char.IsUpper) ? value : value.ToLowerInvariant();

    [GeneratedRegex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*", RegexOptions.Compiled)]
    private static partial Regex Words();

    internal sealed record SpellingPair(string Misspelled, string Correct);
    private sealed record HistoryStore(List<CorrectWordEntry> Words);
    private sealed record CorrectWordEntry(string CorrectWord, int TotalAttempts, DateTimeOffset LastAttemptedUtc,
        List<VariantEntry> Variants)
    {
        public int TotalAttempts { get; set; } = TotalAttempts;
        public DateTimeOffset LastAttemptedUtc { get; set; } = LastAttemptedUtc;
        public CorrectWordEntry Copy() => new(CorrectWord, TotalAttempts, LastAttemptedUtc,
            Variants.Select(x => new VariantEntry(x.Misspelling, x.Count)).ToList());
    }
    private sealed record VariantEntry(string Misspelling, int Count)
    {
        public int Count { get; set; } = Count;
    }

    private static class SpellingWorkbookWriter
    {
        private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public static void Write(string path, IReadOnlyList<CorrectWordEntry> entries)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteText(archive, "[Content_Types].xml", ContentTypes());
            WriteText(archive, "_rels/.rels", RootRelationships());
            WriteText(archive, "xl/workbook.xml", Workbook());
            WriteText(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships());
            WriteText(archive, "xl/styles.xml", Styles());
            WriteSheet(archive, entries);
        }

        private static void WriteSheet(ZipArchive archive, IReadOnlyList<CorrectWordEntry> entries)
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = XmlWriter.Create(stream, Settings());
            writer.WriteStartDocument();
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            writer.WriteAttributeString("xmlns", "r", null, RelationshipNamespace);
            writer.WriteStartElement("sheetViews");
            writer.WriteStartElement("sheetView");
            writer.WriteAttributeString("showGridLines", "0");
            writer.WriteAttributeString("workbookViewId", "0");
            writer.WriteStartElement("pane");
            writer.WriteAttributeString("ySplit", "5");
            writer.WriteAttributeString("topLeftCell", "A6");
            writer.WriteAttributeString("activePane", "bottomLeft");
            writer.WriteAttributeString("state", "frozen");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteElementString("sheetFormatPr", string.Empty);
            writer.WriteStartElement("cols");
            WriteColumn(writer, 1, 1, 9); WriteColumn(writer, 2, 2, 25); WriteColumn(writer, 3, 3, 16);
            WriteColumn(writer, 4, 4, 58); WriteColumn(writer, 5, 5, 23);
            writer.WriteEndElement();
            writer.WriteStartElement("sheetData");
            WriteRow(writer, 2, [("A2", "Spelling history", 1, false)]);
            WriteRow(writer, 3, [("A3", "Ranked by total misspelling attempts. Variants are kept together for future spelling-test practice.", 3, false)]);
            WriteRow(writer, 5,
            [
                ("A5", "Rank", 2, false), ("B5", "Correct word", 2, false),
                ("C5", "Total attempts", 2, false), ("D5", "Misspelled variants", 2, false),
                ("E5", "Last attempted (UTC)", 2, false)
            ]);
            var ordered = entries.OrderByDescending(x => x.TotalAttempts).ThenBy(x => x.CorrectWord, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var item = ordered[i];
                var rank = 1 + ordered.Count(x => x.TotalAttempts > item.TotalAttempts);
                var row = i + 6;
                var variants = string.Join("; ", item.Variants.OrderByDescending(x => x.Count)
                    .ThenBy(x => x.Misspelling, StringComparer.OrdinalIgnoreCase)
                    .Select(x => $"{x.Misspelling} ({x.Count})"));
                WriteRow(writer, row,
                [
                    ($"A{row}", rank.ToString(), 0, true), ($"B{row}", item.CorrectWord, 0, false),
                    ($"C{row}", item.TotalAttempts.ToString(), 0, true), ($"D{row}", variants, 0, false),
                    ($"E{row}", item.LastAttemptedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"), 0, false)
                ]);
            }
            writer.WriteEndElement();
            writer.WriteStartElement("mergeCells"); writer.WriteAttributeString("count", "2");
            writer.WriteStartElement("mergeCell"); writer.WriteAttributeString("ref", "A2:E2"); writer.WriteEndElement();
            writer.WriteStartElement("mergeCell"); writer.WriteAttributeString("ref", "A3:E3"); writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("autoFilter");
            writer.WriteAttributeString("ref", $"A5:E{Math.Max(5, entries.Count + 5)}");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        private static void WriteRow(XmlWriter writer, int rowNumber, IEnumerable<(string Reference, string Value, int Style, bool Numeric)> cells)
        {
            writer.WriteStartElement("row"); writer.WriteAttributeString("r", rowNumber.ToString());
            foreach (var cell in cells)
            {
                writer.WriteStartElement("c"); writer.WriteAttributeString("r", cell.Reference);
                if (cell.Style > 0) writer.WriteAttributeString("s", cell.Style.ToString());
                if (cell.Numeric) writer.WriteElementString("v", cell.Value);
                else
                {
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteStartElement("is"); writer.WriteStartElement("t");
                    writer.WriteAttributeString("xml", "space", null, "preserve"); writer.WriteString(cell.Value);
                    writer.WriteEndElement(); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static void WriteColumn(XmlWriter writer, int min, int max, double width)
        {
            writer.WriteStartElement("col"); writer.WriteAttributeString("min", min.ToString());
            writer.WriteAttributeString("max", max.ToString()); writer.WriteAttributeString("width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1"); writer.WriteEndElement();
        }

        private static void WriteText(ZipArchive archive, string path, string content)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        private static XmlWriterSettings Settings() => new() { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
        private static string ContentTypes() => """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """;
        private static string RootRelationships() => """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """;
        private static string Workbook() => """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Spelling history" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """;
        private static string WorkbookRelationships() => """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """;
        private static string Styles() => """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="4">
                <font><sz val="11"/><name val="Arial"/></font>
                <font><b/><sz val="16"/><color rgb="FF1F2937"/><name val="Arial"/></font>
                <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Arial"/></font>
                <font><i/><sz val="10"/><color rgb="FF6B7280"/><name val="Arial"/></font>
              </fonts>
              <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF4F46E5"/><bgColor indexed="64"/></patternFill></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="4">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"><alignment vertical="center"/></xf>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"><alignment vertical="center"/></xf>
                <xf numFmtId="0" fontId="2" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center"/></xf>
                <xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"><alignment vertical="center"/></xf>
              </cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
            </styleSheet>
            """;
    }
}
