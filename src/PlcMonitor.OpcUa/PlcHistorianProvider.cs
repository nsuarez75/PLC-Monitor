using System.Buffers.Binary;
using Opc.Ua;
using Opc.Ua.Server.Historian;
using PlcMonitor.Historian;

namespace PlcMonitor.OpcUa;

/// <summary>
/// HistoryRead sobre el historiador del agente: lectura en bruto paginada; los agregados los calcula la pila a partir de
/// ella. Solo lectura: insertar, reemplazar o borrar por OPC UA devuelve BadHistoryOperationUnsupported.
/// </summary>
internal sealed class PlcHistorianProvider(PlcNodeManager manager, HistorianService? historian) : HistorianProviderBase, IHistorianDataProvider
{
    /// <summary>Máximo de muestras por página (y por lectura inversa, que no se pagina).</summary>
    internal const int MaxPage = 1000;

    public override ValueTask<bool> IsHistorizingAsync(NodeId nodeId, CancellationToken ct) =>
        new(manager.FindTag(nodeId) is { Definition.IsHistorized: true });

    public async ValueTask<HistorianPage<HistoricalDataValue>> ReadRawAsync(
        HistorianOperationContext context, HistorianRawReadRequest request, HistorianResumeToken resumeToken, CancellationToken ct)
    {
        if (historian is null || manager.FindTag(request.NodeId) is not { } tag)
            return new HistorianPage<HistoricalDataValue>([]);
        var limit = Limit(request.MaxValues, request.PageLimit);
        var (start, end) = ((DateTime)request.StartTime, (DateTime)request.EndTime);
        // Sin final (lectura abierta con un número de valores), hasta ahora; en inversa el principio es el final.
        if (end == DateTime.MinValue)
            end = request.IsForward ? DateTime.UtcNow : DateTime.MinValue;
        var from = new DateTimeOffset(DateTime.SpecifyKind(start < end ? start : end, DateTimeKind.Utc));
        var to = new DateTimeOffset(DateTime.SpecifyKind(start < end ? end : start, DateTimeKind.Utc));
        if (!resumeToken.IsEmpty)
            from = Decode(resumeToken);
        var series = await historian.QueryAsync(new HistoryQuery([tag.Key], from, to, MaxPoints: request.IsForward ? limit : MaxPage), ct);
        var points = series.Count == 0 ? [] : series[0].Points;
        var dataType = tag.Variable.DataType;
        var values = points.Select(p => new HistoricalDataValue(new DataValue(
            OpcUaMapping.ToVariant(p, dataType),
            OpcUaMapping.ToStatus(p.Quality),
            (DateTimeUtc)p.Timestamp.UtcDateTime,
            (DateTimeUtc)p.Timestamp.UtcDateTime))).ToList();
        if (!request.IsForward)
        {
            values.Reverse();
            return new HistorianPage<HistoricalDataValue>(values.Take(limit).ToArray());
        }
        // Página llena y quedan más: se sigue justo después de la última muestra devuelta.
        var next = series.Count > 0 && series[0].Truncated && points.Count > 0 ? Encode(points[^1].Timestamp.AddTicks(1)) : default;
        return new HistorianPage<HistoricalDataValue>(values.ToArray(), next);
    }

    private static int Limit(uint maxValues, uint pageLimit)
    {
        var limit = MaxPage;
        if (maxValues > 0)
            limit = (int)Math.Min(limit, maxValues);
        if (pageLimit > 0)
            limit = (int)Math.Min(limit, pageLimit);
        return limit;
    }

    private static HistorianResumeToken Encode(DateTimeOffset next)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, next.UtcTicks);
        return new HistorianResumeToken(ByteString.From(bytes));
    }

    private static DateTimeOffset Decode(HistorianResumeToken token) =>
        new(BinaryPrimitives.ReadInt64LittleEndian(token.State.Span), TimeSpan.Zero);

    public ValueTask<HistorianUpdateOutcome<DataValue>> InsertAsync(HistorianOperationContext context, NodeId nodeId, ArrayOf<DataValue> values, CancellationToken ct) =>
        Unsupported(values.Count);

    public ValueTask<HistorianUpdateOutcome<DataValue>> ReplaceAsync(HistorianOperationContext context, NodeId nodeId, ArrayOf<DataValue> values, CancellationToken ct) =>
        Unsupported(values.Count);

    public ValueTask<HistorianUpdateOutcome<DataValue>> UpdateAsync(HistorianOperationContext context, NodeId nodeId, ArrayOf<DataValue> values, CancellationToken ct) =>
        Unsupported(values.Count);

    public ValueTask<HistorianUpdateOutcome<DataValue>> DeleteRawAsync(HistorianOperationContext context, NodeId nodeId, DateTimeUtc startTime, DateTimeUtc endTime,
        bool isDeleteModified, CancellationToken ct) =>
        Unsupported(1);

    public ValueTask<HistorianUpdateOutcome<DataValue>> DeleteAtTimeAsync(HistorianOperationContext context, NodeId nodeId, ArrayOf<DateTimeUtc> timestamps, CancellationToken ct) =>
        Unsupported(timestamps.Count);

    private static ValueTask<HistorianUpdateOutcome<DataValue>> Unsupported(int count) =>
        new(new HistorianUpdateOutcome<DataValue>(RepeatStatus(StatusCodes.BadHistoryOperationUnsupported, Math.Max(count, 1))));
}
