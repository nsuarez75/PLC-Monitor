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

    /// <summary>Reparto de los valores con media, desviación y capacidad (Cp/Cpk) frente a las tolerancias.</summary>
    Histogram,

    /// <summary>Valor actual de cada tag en grande, con aguja o barra de nivel y límites.</summary>
    Gauges,

    /// <summary>Barras por hora, turno o día sacadas del histórico del agente.</summary>
    Bars,
}

/// <summary>Cómo se dibuja cada indicador de panel.</summary>
public enum GaugeStyle
{
    /// <summary>Solo el valor en grande.</summary>
    Value,

    /// <summary>Aguja sobre un arco.</summary>
    Needle,

    /// <summary>Barra de nivel vertical.</summary>
    Level,
}

/// <summary>Periodo de las barras del histórico.</summary>
public enum BarPeriod
{
    Hour,

    /// <summary>Turnos de 8 h desde la hora de inicio del primero.</summary>
    Shift,

    Day,
}

/// <summary>Qué se calcula en cada periodo de las barras.</summary>
public enum BarMetric
{
    Max,
    Average,
    Min,

    /// <summary>Lo que ha subido un contador (con sus reinicios).</summary>
    Increase,
}

/// <summary>De dónde salen los datos de una vista.</summary>
public enum TrendDataSource
{
    /// <summary>Lo que llega del agente mientras se está conectado (la app guarda la última hora).</summary>
    Live,

    /// <summary>El histórico del agente en un periodo.</summary>
    History,

    /// <summary>Un CSV exportado del histórico (sin conexión).</summary>
    Csv,
}
