namespace PlcMonitor.Abstractions;

/// <summary>Conexión que permite descubrir las variables del dispositivo.</summary>
public interface IBrowsableConnection
{
    /// <summary>Busca variables cuyo nombre contenga <see cref="BrowseQuery.Filter"/>.</summary>
    Task<BrowseResult> BrowseAsync(BrowseQuery query, CancellationToken cancellationToken = default);
}

/// <param name="Filter">Texto a buscar, sin distinguir mayúsculas. Vacío = todas.</param>
/// <param name="MaxResults">Máximo de resultados devueltos.</param>
/// <param name="Refresh">Releer los símbolos del dispositivo en lugar de usar la caché.</param>
public sealed record BrowseQuery(string? Filter = null, int MaxResults = 1000, bool Refresh = false);

/// <param name="Items">Variables encontradas (como mucho MaxResults).</param>
/// <param name="TotalMatches">Número total de coincidencias.</param>
/// <param name="TotalSymbols">Número total de variables del dispositivo.</param>
public sealed record BrowseResult(IReadOnlyList<BrowseItem> Items, int TotalMatches, int TotalSymbols);

/// <param name="Address">Dirección utilizable en lectura y escritura.</param>
/// <param name="DataType">Tipo normalizado.</param>
/// <param name="NativeType">Tipo en la nomenclatura del dispositivo (p.ej. "Int", "Real", "String").</param>
/// <param name="Detail">Información adicional específica del driver.</param>
public sealed record BrowseItem(string Address, TagDataType DataType, string NativeType, string? Detail = null);
