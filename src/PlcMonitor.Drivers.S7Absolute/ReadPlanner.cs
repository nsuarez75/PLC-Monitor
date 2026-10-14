namespace PlcMonitor.Drivers.S7Absolute;

/// <summary>Bloque contiguo de bytes a leer de un área/DB, con los elementos que contiene.</summary>
internal sealed record ReadBlock(S7Area Area, int DbNumber, int Start, int Length, IReadOnlyList<(int Index, S7Address Address)> Items);

/// <summary>
/// Agrupa direcciones cercanas del mismo área/DB en bloques, para leerlas con menos peticiones.
/// Dos rangos se unen si el hueco entre ellos no supera <c>maxGap</c> y el bloque resultante no pasa de <c>maxBlock</c> bytes.
/// </summary>
internal static class ReadPlanner
{
    public static List<ReadBlock> Plan(IReadOnlyList<(int Index, S7Address Address)> items, int maxGap = 32, int maxBlock = 4096)
    {
        var blocks = new List<ReadBlock>();
        foreach (var group in items.GroupBy(i => (i.Address.Area, i.Address.DbNumber)))
        {
            var sorted = group.OrderBy(i => i.Address.ByteOffset).ThenByDescending(i => i.Address.ByteLength).ToList();
            var start = sorted[0].Address.ByteOffset;
            var end = start + sorted[0].Address.ByteLength;
            var current = new List<(int, S7Address)> { sorted[0] };

            foreach (var item in sorted.Skip(1))
            {
                var itemStart = item.Address.ByteOffset;
                var itemEnd = itemStart + item.Address.ByteLength;
                var newEnd = Math.Max(end, itemEnd);
                if (itemStart <= end + maxGap && newEnd - start <= maxBlock)
                {
                    end = newEnd;
                    current.Add(item);
                }
                else
                {
                    blocks.Add(new ReadBlock(group.Key.Area, group.Key.DbNumber, start, end - start, current));
                    start = itemStart;
                    end = itemEnd;
                    current = [item];
                }
            }
            blocks.Add(new ReadBlock(group.Key.Area, group.Key.DbNumber, start, end - start, current));
        }
        return blocks;
    }
}
