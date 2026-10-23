using System.ComponentModel;
using System.Globalization;

namespace PlcMonitor.App.Localization;

/// <summary>Texto de la interfaz que se actualiza solo al cambiar de idioma.</summary>
public sealed class LocalizedValue : INotifyPropertyChanged
{
    private readonly string _source;

    internal LocalizedValue(string source) => _source = source;

    public string Value => Loc.T(_source);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>
/// Textos de la interfaz en castellano (idioma de origen) e inglés. Las claves son el propio texto en castellano,
/// como en gettext: si una cadena no está traducida se muestra tal cual.
/// </summary>
public static class Loc
{
    public const string Spanish = "es";
    public const string English = "en";

    private static readonly Dictionary<string, LocalizedValue> Values = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static string _language = Spanish;

    public static string Language => _language;

    public static event EventHandler? LanguageChanged;

    public static bool IsEnglish => _language == English;

    public static string T(string source)
    {
        if (_language != English)
            return source;
        return EnglishStrings.Map.TryGetValue(source, out var text) ? text : source;
    }

    public static string F(string source, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(source), args);

    /// <summary>Texto enlazable desde XAML; se refresca al cambiar de idioma.</summary>
    public static LocalizedValue Value(string source)
    {
        lock (Gate)
        {
            if (!Values.TryGetValue(source, out var value))
                Values[source] = value = new LocalizedValue(source);
            return value;
        }
    }

    /// <summary>Cambia el idioma ("es" o "en"); cualquier otro valor vuelve al castellano.</summary>
    public static void SetLanguage(string? language)
    {
        var next = string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Spanish;
        if (next == _language)
            return;
        _language = next;
        LocalizedValue[] values;
        lock (Gate)
            values = [.. Values.Values];
        foreach (var value in values)
            value.Refresh();
        LocalizedObservableObject.RefreshAll();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Normaliza el idioma guardado en las preferencias.</summary>
    public static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ? English : Spanish;
}
