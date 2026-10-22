namespace PlcMonitor.Drivers.Modbus.Tests;

public class ReadPlannerTests
{
    private static List<(int, ModbusAddress)> Items(params string[] addresses) =>
        addresses.Select((a, i) => (i, ModbusAddress.Parse(a))).ToList();

    [Fact]
    public void Merges_close_registers_of_the_same_table()
    {
        var blocks = ReadPlanner.Plan(Items("HR0", "HR1:FLOAT", "HR5", "HR100", "IR0"), maxGap: 8);
        blocks.Count.ShouldBe(3);
        var first = blocks.Single(b => b.Table == ModbusTable.HoldingRegister && b.Start == 0);
        first.Count.ShouldBe(6);
        first.Items.Count.ShouldBe(3);
        blocks.ShouldContain(b => b.Table == ModbusTable.HoldingRegister && b.Start == 100 && b.Count == 1);
        blocks.ShouldContain(b => b.Table == ModbusTable.InputRegister && b.Start == 0);
    }

    [Fact]
    public void Gap_zero_only_merges_contiguous()
    {
        ReadPlanner.Plan(Items("HR0", "HR1", "HR3"), maxGap: 0).Count.ShouldBe(2);
        ReadPlanner.Plan(Items("HR0", "HR1", "HR2"), maxGap: 0).Count.ShouldBe(1);
    }

    [Fact]
    public void Respects_the_registers_per_request_limit()
    {
        var blocks = ReadPlanner.Plan(Items("HR0", "HR124"), maxGap: 200, maxRegisters: 125);
        blocks.Count.ShouldBe(1);
        ReadPlanner.Plan(Items("HR0", "HR125"), maxGap: 200, maxRegisters: 125).Count.ShouldBe(2);
        ReadPlanner.Plan(Items("HR0:DOUBLE", "HR4:DOUBLE"), maxGap: 8, maxRegisters: 6).Count.ShouldBe(2);
    }

    [Fact]
    public void Bit_tables_use_the_bits_limit()
    {
        ReadPlanner.Plan(Items("C0", "C1999"), maxGap: 3000, maxRegisters: 125, maxBits: 2000).Count.ShouldBe(1);
        ReadPlanner.Plan(Items("C0", "C2000"), maxGap: 3000, maxRegisters: 125, maxBits: 2000).Count.ShouldBe(2);
    }

    [Fact]
    public void Overlapping_tags_share_the_block()
    {
        var blocks = ReadPlanner.Plan(Items("HR10:INT32", "HR10", "HR11", "HR10.3"));
        blocks.Count.ShouldBe(1);
        blocks[0].Start.ShouldBe(10);
        blocks[0].Count.ShouldBe(2);
        blocks[0].Items.Count.ShouldBe(4);
    }
}
