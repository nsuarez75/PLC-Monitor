using Avalonia.Data.Converters;
using PlcMonitor.App.Localization;

namespace PlcMonitor.App.ViewModels;

/// <summary>Convertidores para la vista.</summary>
public static class Converters
{
    /// <summary>true si el valor es el entero 0.</summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);

    /// <summary>true si el valor es un entero mayor que 0.</summary>
    public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>Traduce un texto fijo (clave en castellano) para mostrarlo en el idioma elegido.</summary>
    public static readonly IValueConverter Localized = new FuncValueConverter<string?, string?>(text => text is null ? null : Loc.T(text));
}
