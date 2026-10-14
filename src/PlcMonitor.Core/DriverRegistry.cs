using PlcMonitor.Abstractions;

namespace PlcMonitor.Core;

/// <summary>Catálogo de drivers disponibles, indexado por <see cref="IDriverFactory.TypeId"/>.</summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDriverFactory> _factories = new(StringComparer.OrdinalIgnoreCase);

    public DriverRegistry(IEnumerable<IDriverFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        foreach (var factory in factories)
            Register(factory);
    }

    public IReadOnlyCollection<IDriverFactory> Factories => _factories.Values;

    public void Register(IDriverFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (string.IsNullOrWhiteSpace(factory.TypeId))
            throw new ArgumentException("El driver no tiene TypeId.", nameof(factory));
        if (!_factories.TryAdd(factory.TypeId, factory))
            throw new InvalidOperationException($"Ya hay un driver registrado con el identificador '{factory.TypeId}'.");
    }

    public bool TryGet(string typeId, out IDriverFactory factory) => _factories.TryGetValue(typeId, out factory!);

    public IDriverFactory Get(string typeId) =>
        TryGet(typeId, out var factory)
            ? factory
            : throw new KeyNotFoundException(
                $"Driver desconocido '{typeId}'. Disponibles: {string.Join(", ", _factories.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
}
