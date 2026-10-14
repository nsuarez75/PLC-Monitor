namespace PlcMonitor.Drivers.S7Absolute.Tests;

public class ReadPlannerTests
{
    private static List<(int, S7Address)> Items(params string[] addresses) =>
        addresses.Select((a, i) => (i, S7Address.Parse(a))).ToList();

    [Fact]
    public void Merges_close_addresses_in_same_db()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.DBW0", "DB1.REAL10", "DB1.DBX4.1"), maxGap: 32);
        var block = blocks.ShouldHaveSingleItem();
        block.Start.ShouldBe(0);
        block.Length.ShouldBe(14);
        block.Items.Count.ShouldBe(3);
    }

    [Fact]
    public void Splits_when_gap_is_too_large()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.DBW0", "DB1.DBW100"), maxGap: 32);
        blocks.Count.ShouldBe(2);
        blocks.Select(b => (b.Start, b.Length)).ShouldBe([(0, 2), (100, 2)]);
    }

    [Fact]
    public void Never_merges_different_areas_or_dbs()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.DBW0", "DB2.DBW0", "MW0", "IW0", "QW0"));
        blocks.Count.ShouldBe(5);
    }

    [Fact]
    public void Overlapping_addresses_share_a_block()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.DBD0", "DB1.DBW2", "DB1.DBX3.7"));
        var block = blocks.ShouldHaveSingleItem();
        block.Length.ShouldBe(4);
    }

    [Fact]
    public void Respects_max_block_size()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.B0[100]", "DB1.B100[100]"), maxGap: 32, maxBlock: 150);
        blocks.Count.ShouldBe(2);
    }

    [Fact]
    public void Keeps_original_indexes()
    {
        var blocks = ReadPlanner.Plan(Items("DB1.DBW10", "DB1.DBW0"));
        blocks.ShouldHaveSingleItem().Items.Select(i => i.Index).ShouldBe([1, 0]);
    }
}
