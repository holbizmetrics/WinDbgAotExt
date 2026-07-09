using System;
using System.Collections.Generic;
using System.Threading;

// A controlled managed debuggee for exercising debugger.Heap: it allocates a known, distinctive set
// of objects, keeps them rooted, prints its PID, then parks so a debugger can attach and walk the heap.
var widgets = new List<Widget>();
for (int index = 0; index < 1000; index++)
    widgets.Add(new Widget { Id = index, Label = "widget-" + index });

var payloads = new List<byte[]>();
for (int index = 0; index < 50; index++)
    payloads.Add(new byte[4096]);   // 50 distinctive 4 KB arrays

Console.WriteLine("HeapTarget PID=" + Environment.ProcessId);
Console.WriteLine("Allocated 1000 Widget + 50 byte[4096]. Parked; attach the debugger now.");
GC.Collect();
GC.KeepAlive(widgets);
GC.KeepAlive(payloads);
while (true) Thread.Sleep(1000);

sealed class Widget
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
}
