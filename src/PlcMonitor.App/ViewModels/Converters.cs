using Avalonia.Data.Converters;

namespace PlcMonitor.App.ViewModels;

/// <summary>Convertidores para la vista.</summary>
public static class Converters
{
    /// <summary>true si el valor es el entero 0.</summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
}
