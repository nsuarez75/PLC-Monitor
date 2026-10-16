using System.Globalization;
using System.Text;

namespace PlcMonitor.App.Charts;

/// <summary>
/// Lee el CSV que exporta la pestaña Histórico (<c>device,tag,timestamp,value,quality</c>; hora UTC ISO 8601) para verlo
/// sin conexión con el agente. Los valores numéricos y booleanos se dibujan; los de texto se ignoran; una calidad mala
/// deja un hueco.
/// </summary>
public static class HistoryCsv
{
    public const string Header = "device,tag,timestamp,value,quality";

    /// <summary>Series del fichero, una por tag (con «dispositivo/tag» si hay varios dispositivos).</summary>
    public static HistoryCsvContent Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var header = reader.ReadLine()?.Trim().TrimStart('﻿');
        if (!string.Equals(header, Header, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"No es un CSV de histórico de PLC-Monitor: la primera línea debe ser «{Header}».");

        var rows = new List<(string Device, string Tag, DateTimeOffset Time, string Value, string Quality)>();
        var skipped = 0;
        var lineNumber = 1;
        while (ReadRecord(reader, ref lineNumber) is { } fields)
        {
            if (fields.Count == 1 && fields[0].Length == 0)
                continue;
            if (fields.Count < 5 || !DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                skipped++;
                continue;
            }
            rows.Add((fields[0], fields[1], time, fields[3], fields[4]));
        }

        var multiDevice = rows.Select(r => r.Device).Distinct(StringComparer.Ordinal).Count() > 1;
        var series = new List<ChartSeriesData>();
        foreach (var group in rows.GroupBy(r => (r.Device, r.Tag)).OrderBy(g => g.Key.Device, StringComparer.Ordinal).ThenBy(g => g.Key.Tag, StringComparer.Ordinal))
        {
            var values = group.OrderBy(r => r.Time).ToArray();
            var isBoolean = values.Where(v => IsGood(v.Quality)).All(v => bool.TryParse(v.Value, out _)) && values.Any(v => IsGood(v.Quality));
            var points = new List<ChartPoint>(values.Length);
            var numeric = false;
            foreach (var v in values)
            {
                double? number = !IsGood(v.Quality) ? null
                    : bool.TryParse(v.Value, out var b) ? (b ? 1 : 0)
                    : double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                    : null;
                numeric |= number is not null;
                points.Add(new ChartPoint(v.Time, number));
            }
            if (numeric)
                series.Add(new ChartSeriesData(multiDevice ? $"{group.Key.Device}/{group.Key.Tag}" : group.Key.Tag, points, Step: true, isBoolean));
        }
        var devices = rows.Select(r => r.Device).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new HistoryCsvContent(series, devices, rows.Count, skipped,
            rows.Count == 0 ? null : rows.Min(r => r.Time), rows.Count == 0 ? null : rows.Max(r => r.Time));
    }

    public static HistoryCsvContent Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    private static bool IsGood(string quality) =>
        quality.Equals("Good", StringComparison.OrdinalIgnoreCase) || quality.Equals("Uncertain", StringComparison.OrdinalIgnoreCase);

    /// <summary>Un registro CSV (RFC 4180: comillas dobles, comillas escapadas y saltos de línea dentro de comillas).</summary>
    private static List<string>? ReadRecord(TextReader reader, ref int lineNumber)
    {
        var line = reader.ReadLine();
        if (line is null)
            return null;
        lineNumber++;
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var i = 0;
        while (true)
        {
            if (i >= line.Length)
            {
                if (quoted)
                {
                    // Salto de línea dentro de un campo entre comillas.
                    var next = reader.ReadLine();
                    if (next is null)
                        break;
                    lineNumber++;
                    field.Append('\n');
                    line = next;
                    i = 0;
                    continue;
                }
                break;
            }
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i += 2;
                    continue;
                }
                if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
            i++;
        }
        fields.Add(field.ToString());
        return fields;
    }
}

/// <summary>Contenido de un CSV de histórico.</summary>
public sealed record HistoryCsvContent(
    IReadOnlyList<ChartSeriesData> Series, IReadOnlyList<string> Devices, int Rows, int SkippedRows, DateTimeOffset? From, DateTimeOffset? To);
