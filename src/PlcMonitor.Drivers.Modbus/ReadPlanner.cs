namespace PlcMonitor.Drivers.Modbus;

/// <summary>Bloque contiguo de registros (o bits) de una tabla, con los tags que contiene.</summary>
internal sealed record ReadBlock(ModbusTable Table, int Start, int Count, IReadOnlyList<(int Index, ModbusAddress Address)> Items);

/// <summary>
/// Agrupa direcciones cercanas de la misma tabla en bloques, para leerlas con menos peticiones (funciones 01/02/03/04).
/// Dos rangos se unen si el hueco entre ellos no supera <c>maxGap</c> y el bloque no pasa del máximo por petición:
/// <c>maxRegisters</c> en las tablas de registros y <c>maxBits</c> en las de bits.
/// </summary>
internal static class ReadPlanner
{
    public static List<ReadBlock> Plan(
        IReadOnlyList<(int Index, ModbusAddress Address)> items, int maxGap = 8, int maxRegisters = 125, int maxBits = 2000)
    {
        var blocks = new List<ReadBlock>();
        foreach (var group in items.GroupBy(i => i.Address.Table))
        {
            var max = group.First().Address.IsBitTable ? maxBits : maxRegisters;
            var sorted = group.OrderBy(i => i.Address.Offset).ThenByDescending(i => i.Address.Count).ToList();
            var start = sorted[0].Address.Offset;
            var end = start + sorted[0].Address.Count;
            var current = new List<(int, ModbusAddress)> { sorted[0] };

            foreach (var item in sorted.Skip(1))
            {
                var itemStart = item.Address.Offset;
                var itemEnd = itemStart + item.Address.Count;
                var newEnd = Math.Max(end, itemEnd);
                if (itemStart <= end + maxGap && newEnd - start <= max)
                {
                    end = newEnd;
                    current.Add(item);
                }
                else
                {
                    blocks.Add(new ReadBlock(group.Key, start, end - start, current));
                    start = itemStart;
                    end = itemEnd;
                    current = [item];
                }
            }
            blocks.Add(new ReadBlock(group.Key, start, end - start, current));
        }
        return blocks;
    }
}
