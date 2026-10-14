// Spike de viabilidad: SOLO LECTURA. Conecta (TLS + legitimación), explora y lee.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

string host = args.Length > 0 ? args[0] : "192.168.2.1";
string password = args.Length > 1 ? args[1] : "";
string filter = args.Length > 2 ? args[2] : "CNV02_IDB.Sequence";

var sw = Stopwatch.StartNew();
var conn = new S7CommPlusConnection();
int res = conn.Connect(host, password, "", 10000);
Console.WriteLine($"Connect res={res} t={sw.ElapsedMilliseconds} ms");
if (res != 0) return 1;

try
{
    sw.Restart();
    res = conn.GetListOfDatablocks(out List<S7CommPlusConnection.DatablockInfo> dbs);
    Console.WriteLine($"GetListOfDatablocks res={res} count={dbs?.Count} t={sw.ElapsedMilliseconds} ms");

    sw.Restart();
    res = conn.Browse(out List<VarInfo> vars);
    Console.WriteLine($"Browse res={res} vars={vars?.Count} t={sw.ElapsedMilliseconds} ms");
    if (res != 0 || vars == null) return 2;

    var sel = vars.Where(v => v.Name.Contains(filter)).Take(15).ToList();
    Console.WriteLine($"Variables que contienen '{filter}': {vars.Count(v => v.Name.Contains(filter))} (leo {sel.Count})");

    var tags = sel.Select(v => PlcTags.TagFactory(v.Name, new ItemAddress(v.AccessSequence), v.Softdatatype)).ToList();
    for (int i = 0; i < 3; i++)
    {
        sw.Restart();
        res = conn.ReadTags(tags);
        Console.WriteLine($"ReadTags #{i + 1} res={res} t={sw.ElapsedMilliseconds} ms");
    }
    foreach (var t in tags)
        Console.WriteLine($"  {t.Name,-70} {t.Address.GetAccessString(),-28} {Softdatatype.Types[t.Datatype],-10} {t}");
}
finally
{
    conn.Disconnect();
    Console.WriteLine("Disconnected");
}
return 0;
