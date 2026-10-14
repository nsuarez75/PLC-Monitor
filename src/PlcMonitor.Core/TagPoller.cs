using System.Runtime.CompilerServices;
using PlcMonitor.Abstractions;

namespace PlcMonitor.Core;

/// <summary>Cambio de valor de un tag.</summary>
public sealed record TagChange(string Address, TagValue Value);

/// <summary>
/// Sondeo periódico de un conjunto de direcciones con detección de cambios.
/// Funciona con cualquier driver; es la base de los grupos de escaneo.
/// </summary>
public static class TagPoller
{
    /// <summary>
    /// Lee <paramref name="addresses"/> cada <paramref name="interval"/>. La primera vez emite todos los valores;
    /// después solo los que cambian de valor o de calidad. Si una lectura falla por completo, todos los tags pasan
    /// a calidad mala y se sigue intentando.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<TagChange>> WatchAsync(
        IDeviceConnection connection,
        IReadOnlyList<string> addresses,
        TimeSpan interval,
        TimeProvider? timeProvider = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        timeProvider ??= TimeProvider.System;

        var last = new TagValue?[addresses.Count];
        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            var values = await ReadSafeAsync(connection, addresses, timeProvider, cancellationToken);
            var changes = DetectChanges(addresses, last, values);
            if (changes.Count > 0)
                yield return changes;
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    internal static List<TagChange> DetectChanges(IReadOnlyList<string> addresses, TagValue?[] last, IReadOnlyList<TagValue> values)
    {
        var changes = new List<TagChange>();
        for (var i = 0; i < addresses.Count; i++)
        {
            var value = values[i];
            if (!value.HasSameValueAndQuality(last[i]))
            {
                changes.Add(new TagChange(addresses[i], value));
                last[i] = value;
            }
        }
        return changes;
    }

    private static async Task<IReadOnlyList<TagValue>> ReadSafeAsync(
        IDeviceConnection connection, IReadOnlyList<string> addresses, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            var values = await connection.ReadAsync(addresses, cancellationToken);
            if (values.Count != addresses.Count)
                throw new InvalidOperationException($"El driver devolvió {values.Count} valores para {addresses.Count} direcciones.");
            return values;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Cualquier fallo de lectura se refleja como calidad mala, no detiene el sondeo.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var quality = connection.State == ConnectionState.Connected ? QualityCode.Bad : QualityCode.BadNotConnected;
            var bad = TagValue.Bad(quality, timeProvider.GetUtcNow(), ex.Message);
            return Enumerable.Repeat(bad, addresses.Count).ToArray();
        }
    }
}
