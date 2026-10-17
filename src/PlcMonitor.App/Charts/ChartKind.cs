namespace PlcMonitor.App.Charts;

/// <summary>Tipo de gráfica de una vista de tendencia.</summary>
public enum ChartKind
{
    /// <summary>Valor frente a tiempo (la de siempre).</summary>
    Trend,

    /// <summary>Cada activación del disparo alineada en t = 0 y dibujada encima de las anteriores.</summary>
    Cycles,

    /// <summary>Un tag contra otro (el primero de la vista es el eje X).</summary>
    XY,

    /// <summary>Una franja por tag con un color por estado, con duraciones y porcentajes.</summary>
    States,
}
