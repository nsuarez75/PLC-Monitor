using System.Text.RegularExpressions;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.Tests;

/// <summary>
/// El diccionario de traducciones es el único sitio donde se traduce: estos tests leen el código de la app y fallan si
/// un texto de la interfaz no tiene su traducción, si queda alguno sin pasar por <see cref="Loc"/> o si sobra alguna.
/// </summary>
public partial class LocalizationCatalogTests
{
    /// <summary>Textos de XAML que se ven igual en los dos idiomas (siglas, unidades, ejemplos).</summary>
    private static readonly HashSet<string> NeutralXamlTexts =
    [
        "PLC-Monitor", "PLC", "CPU", "OPC UA", "URL", "Token", "Driver", "Timeout", "CSV", "PNG", "rack", "slot", "bit",
        "bits", "ms", "true", "false", "σ", "Δ", "×", "=", "—", "0", "prensa1", "bar, °C, rpm…", "192.168.0.1",
        "192.168.0.10", "192.168.0.20", "17/10/2026 06:00", "17/10/2026 14:00",
    ];

    private static readonly string AppDir = FindAppDir();

    [Fact]
    public void Every_marked_text_has_its_translation()
    {
        var missing = SourceFiles("*.cs").SelectMany(f => MarkedCs().Matches(f.Text).Select(m => (f.Path, Text: Unescape(m.Groups[1].Value))))
            .Concat(SourceFiles("*.axaml").SelectMany(f => MarkedXaml().Matches(f.Text).Select(m => (f.Path, Text: m.Groups[1].Value))))
            .Where(t => !EnglishStrings.Map.ContainsKey(t.Text))
            .Select(t => $"{t.Path}: «{t.Text}»")
            .Distinct()
            .ToArray();

        missing.ShouldBeEmpty("Añade estos textos a Localization/EnglishStrings.cs");
    }

    [Fact]
    public void No_spanish_text_skips_the_dictionary()
    {
        var keys = EnglishStrings.Map.Keys.ToHashSet(StringComparer.Ordinal);
        var loose = SourceFiles("*.cs")
            .Where(f => Path.GetFileName(f.Path) is not ("Loc.cs" or "EnglishStrings.cs"))
            .SelectMany(f => CodeLines(f.Text).SelectMany(line => Literal().Matches(line))
                .Select(m => (f.Path, Interpolated: m.Groups[1].Success, Text: Unescape(m.Groups[2].Value))))
            .Where(t => !keys.Contains(t.Text) && LooksSpanish(t.Text, t.Interpolated))
            .Select(t => $"{t.Path}: «{t.Text}»")
            .ToArray();

        var xaml = SourceFiles("*.axaml")
            .SelectMany(f => XamlText().Matches(f.Text).Select(m => (f.Path, Text: m.Groups[1].Value)))
            .Where(t => !NeutralXamlTexts.Contains(t.Text))
            .Select(t => $"{t.Path}: «{t.Text}» (usa {{loc:T '…'}})")
            .ToArray();

        loose.Concat(xaml).ShouldBeEmpty("Pasa estos textos por Loc.T/Loc.F/Loc.N y tradúcelos en EnglishStrings.cs");
    }

    [Fact]
    public void Every_translation_is_used()
    {
        var used = SourceFiles("*.cs").Where(f => !f.Path.EndsWith("EnglishStrings.cs", StringComparison.Ordinal))
            .SelectMany(f => CodeLines(f.Text).SelectMany(line => Literal().Matches(line)).Select(m => Unescape(m.Groups[2].Value)))
            .Concat(SourceFiles("*.axaml").SelectMany(f => MarkedXaml().Matches(f.Text).Select(m => m.Groups[1].Value)))
            .ToHashSet(StringComparer.Ordinal);

        EnglishStrings.Map.Keys.Where(k => !used.Contains(k)).ToArray()
            .ShouldBeEmpty("Quita de EnglishStrings.cs los textos que ya no usa la app");
    }

    [Fact]
    public void Translations_keep_the_placeholders()
    {
        EnglishStrings.Map.Where(p => !Placeholders(p.Key).SequenceEqual(Placeholders(p.Value)))
            .Select(p => $"«{p.Key}» → «{p.Value}»").ToArray()
            .ShouldBeEmpty("La traducción debe tener los mismos {0}, {1}… que el original");

        static string[] Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order()];
    }

    /// <summary>Acentos, eñes, signos de apertura o comillas angulares; en textos normales, también palabras sueltas muy comunes.</summary>
    private static bool LooksSpanish(string text, bool interpolated) =>
        SpanishLetters().IsMatch(text) || (!interpolated && SpanishWords().IsMatch(text));

    private static IEnumerable<(string Path, string Text)> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => (Path.GetRelativePath(AppDir, p), File.ReadAllText(p)));

    /// <summary>Líneas de código sin las de comentario (los <c>///</c> llevan ejemplos entre comillas).</summary>
    private static IEnumerable<string> CodeLines(string text) =>
        text.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Unescape(string literal) => Regex.Unescape(literal);

    private static string FindAppDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "src", "PlcMonitor.App");
            if (File.Exists(Path.Combine(dir.FullName, "PlcMonitor.slnx")) && Directory.Exists(app))
                return app;
        }
        throw new DirectoryNotFoundException("No se encuentra src/PlcMonitor.App desde la carpeta de los tests.");
    }

    [GeneratedRegex(@"Loc\.(?:T|F|N|Value)\(\s*""((?:[^""\\\n]|\\.)*)""[\s,)]")]
    private static partial Regex MarkedCs();

    [GeneratedRegex(@"\{loc:T\s+'([^']*)'\}")]
    private static partial Regex MarkedXaml();

    [GeneratedRegex(@"(?<![@""])(\$)?""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\b(?:Text|Content|Header|ToolTip\.Tip|Watermark|PlaceholderText|Title|EmptyText)=""([^""{][^""]*)""")]
    private static partial Regex XamlText();

    [GeneratedRegex("[áéíóúñÁÉÍÓÚÑ¿¡«»]")]
    private static partial Regex SpanishLetters();

    [GeneratedRegex(@"\b(?:el|la|los|las|de|del|con|sin|para|por|un|una|hay|no|se|es|y|en|al)\b")]
    private static partial Regex SpanishWords();

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
