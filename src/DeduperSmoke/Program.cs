using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UsnWatcher.Core;
using UsnWatcher.Host;

// Simple smoke harness to validate Deduper merging behavior without opening a volume.

Console.WriteLine("Deduper smoke harness starting...");

var deduper = new Deduper(50);
var emitted = new List<UsnRecord>();

var t = deduper.StartAsync(r =>
{
    lock (emitted) { emitted.Add(r); }
    Console.WriteLine("EMIT: " + JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = false }));
}, CancellationToken.None);

// Test 1: rapid save sequence (DATA_OVERWRITE, DATA_TRUNCATION, CLOSE)
var frn = 0x1234uL;
var now = DateTime.UtcNow;
var r1 = new UsnRecord { FileReferenceNumber = frn, Usn = 1, Timestamp = now, Reasons = new[] { "DATAOVERWRITE" }, ReasonRaw = 0x1 };
var r2 = new UsnRecord { FileReferenceNumber = frn, Usn = 2, Timestamp = now.AddMilliseconds(5), Reasons = new[] { "DATATRUNCATION" }, ReasonRaw = 0x4 };
var r3 = new UsnRecord { FileReferenceNumber = frn, Usn = 3, Timestamp = now.AddMilliseconds(10), Reasons = new[] { "CLOSE" }, ReasonRaw = 0x80000000 };

deduper.Add(r1);
Thread.Sleep(10);
deduper.Add(r2);
Thread.Sleep(10);
deduper.Add(r3);

// Wait long enough for debounce window to expire
Thread.Sleep(200);

Console.WriteLine("--- After save sequence, emitted count=" + emitted.Count);

// Test 2: rename pair should preserve OldPath/NewPath
emitted.Clear();
var frn2 = 0x2222uL;
var oldPath = "C:\\temp\\oldname.txt";
var newPath = "C:\\temp\\newname.txt";
var ro = new UsnRecord { FileReferenceNumber = frn2, Usn = 10, Timestamp = DateTime.UtcNow, Reasons = new[] { "RENAMEOLDNAME" }, ReasonRaw = 0x1000, OldPath = oldPath };
var rn = new UsnRecord { FileReferenceNumber = frn2, Usn = 11, Timestamp = DateTime.UtcNow.AddMilliseconds(5), Reasons = new[] { "RENAMENEWNAME" }, ReasonRaw = 0x2000, NewPath = newPath };

deduper.Add(ro);
Thread.Sleep(10);
deduper.Add(rn);

Thread.Sleep(200);

Console.WriteLine("--- After rename sequence, emitted count=" + emitted.Count);
foreach (var e in emitted)
{
    Console.WriteLine("EMIT: " + JsonSerializer.Serialize(e));
}

deduper.FlushAll(r => Console.WriteLine("FINAL FLUSH EMIT: " + JsonSerializer.Serialize(r)));
deduper.Dispose();

Console.WriteLine("Done.");
