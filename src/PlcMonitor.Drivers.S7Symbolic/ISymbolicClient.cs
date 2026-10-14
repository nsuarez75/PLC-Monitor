using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcMonitor.Drivers.S7Symbolic;

/// <summary>Símbolo descubierto al explorar la CPU.</summary>
internal readonly record struct SymbolEntry(string Name, uint Softdatatype);

/// <summary>Operaciones de la librería S7CommPlus que usa el driver (sustituible en tests). Llamadas bloqueantes.</summary>
internal interface ISymbolicClient : IDisposable
{
    /// <summary>Devuelve 0 si conecta; si no, el código de error de S7Consts.</summary>
    int Connect(string host, string password, string username, int timeoutMs);

    void Disconnect();

    /// <summary>Resuelve un símbolo a un tag listo para leer/escribir. null si no existe.</summary>
    PlcTag? ResolveTag(string symbol);

    int ReadTags(IReadOnlyList<PlcTag> tags);

    int WriteTags(IReadOnlyList<PlcTag> tags);

    /// <summary>Explora todas las variables de la CPU.</summary>
    int Browse(out IReadOnlyList<SymbolEntry> symbols);
}

/// <summary>Implementación real sobre <see cref="S7CommPlusConnection"/>.</summary>
internal sealed class S7CommPlusClient : ISymbolicClient
{
    private readonly S7CommPlusConnection _connection = new();

    public int Connect(string host, string password, string username, int timeoutMs) =>
        _connection.Connect(host, password, username, timeoutMs);

    public void Disconnect() => _connection.Disconnect();

    public PlcTag? ResolveTag(string symbol) => _connection.getPlcTagBySymbol(symbol);

    public int ReadTags(IReadOnlyList<PlcTag> tags) => _connection.ReadTags(tags);

    public int WriteTags(IReadOnlyList<PlcTag> tags) => _connection.WriteTags(tags);

    public int Browse(out IReadOnlyList<SymbolEntry> symbols)
    {
        var result = _connection.Browse(out var vars);
        symbols = result == 0 && vars is not null
            ? vars.Select(v => new SymbolEntry(v.Name, v.Softdatatype)).ToArray()
            : [];
        return result;
    }

    public void Dispose() => _connection.Disconnect();
}
