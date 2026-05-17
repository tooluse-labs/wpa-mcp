using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: interruptfixture scan <trace.etl> [bucketMs]");
    Console.Error.WriteLine("       interruptfixture make-mixed <input.etl> <output.etl> [stackCount=10] [missingCount=1] [contextUs=5000]");
    Console.Error.WriteLine("       interruptfixture relog-window <input.etl> <output.etl> <startUs> <endUs>");
    Console.Error.WriteLine("       interruptfixture dump-window <input.etl> <startUs> <endUs> [top=50]");
    return 2;
}

return args[0] switch
{
    "scan" => Scan(args),
    "make-mixed" => MakeMixed(args),
    "relog-window" => RelogWindow(args),
    "dump-window" => DumpWindow(args),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("unknown verb");
    return 2;
}

static int Scan(string[] args)
{
    if (args.Length is < 2 or > 3)
    {
        Console.Error.WriteLine("usage: interruptfixture scan <trace.etl> [bucketMs]");
        return 2;
    }

    var path = args[1];
    var bucketUs = (long)((args.Length >= 3 ? double.Parse(args[2]) : 100.0) * 1000);
    using var trace = TraceLog.OpenOrConvert(path);
    var buckets = new Dictionary<long, Bucket>();
    var all = new Bucket();

    foreach (var ev in trace.Events)
    {
        if (!TryInterrupt(ev, out var us, out var hasStack))
            continue;

        var tsUs = (long)(ev.TimeStampRelativeMSec * 1000);
        var key = tsUs / bucketUs * bucketUs;
        var bucket = buckets.GetValueOrDefault(key);
        bucket.Add(us, hasStack);
        buckets[key] = bucket;
        all.Add(us, hasStack);
    }

    Console.WriteLine($"total count={all.TotalCount} stackCount={all.StackCount} noStackCount={all.NoStackCount} totalUs={all.TotalUs} stackUs={all.StackUs} noStackUs={all.NoStackUs}");
    Console.WriteLine("candidate windows where no-stack count < 50% but no-stack us >= 50%:");
    foreach (var kv in buckets
        .Where(kv => kv.Value.TotalCount > 1 &&
                     kv.Value.NoStackCount * 2 < kv.Value.TotalCount &&
                     kv.Value.NoStackUs * 2 >= kv.Value.TotalUs)
        .OrderByDescending(kv => kv.Value.NoStackUs)
        .Take(20))
    {
        var b = kv.Value;
        Console.WriteLine(
            $"startUs={kv.Key} endUs={kv.Key + bucketUs} " +
            $"count={b.TotalCount} stackCount={b.StackCount} noStackCount={b.NoStackCount} " +
            $"totalUs={b.TotalUs} stackUs={b.StackUs} noStackUs={b.NoStackUs}");
    }

    return 0;
}

static int RelogWindow(string[] args)
{
    if (args.Length != 5)
    {
        Console.Error.WriteLine("usage: interruptfixture relog-window <input.etl> <output.etl> <startUs> <endUs>");
        return 2;
    }

    var input = args[1];
    var output = args[2];
    var startUs = long.Parse(args[3]);
    var endUs = long.Parse(args[4]);
    long kept = 0;
    long dropped = 0;

    using var relog = new ETWReloggerTraceEventSource(input, output);
    relog.AllEvents += data =>
    {
        var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
        if (tsUs >= startUs && tsUs < endUs)
        {
            relog.WriteEvent(data);
            kept++;
        }
        else
        {
            dropped++;
        }
    };
    relog.Process();

    Console.WriteLine($"kept={kept} dropped={dropped} outBytes={new FileInfo(output).Length}");
    return 0;
}

static int DumpWindow(string[] args)
{
    if (args.Length is < 4 or > 5)
    {
        Console.Error.WriteLine("usage: interruptfixture dump-window <input.etl> <startUs> <endUs> [top=50]");
        return 2;
    }

    var input = args[1];
    var startUs = long.Parse(args[2]);
    var endUs = long.Parse(args[3]);
    var top = args.Length >= 5 ? int.Parse(args[4]) : 50;
    var count = 0;
    using var source = new ETWTraceEventSource(input);
    source.AllEvents += data =>
    {
        var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
        if (tsUs < startUs || tsUs >= endUs || count >= top)
            return;

        Console.WriteLine(
            $"tsUs={tsUs} provider={data.ProviderName} event={data.EventName} id={data.ID} opcode={data.Opcode} opcodeName={data.OpcodeName} task={data.Task} taskName={data.TaskName}");
        count++;
    };
    source.Process();
    return 0;
}

static int MakeMixed(string[] args)
{
    if (args.Length is < 3 or > 6)
    {
        Console.Error.WriteLine("usage: interruptfixture make-mixed <input.etl> <output.etl> [stackCount=10] [missingCount=1] [contextUs=5000]");
        return 2;
    }

    var input = args[1];
    var output = args[2];
    var stackCount = args.Length >= 4 ? int.Parse(args[3]) : 10;
    var missingCount = args.Length >= 5 ? int.Parse(args[4]) : 1;
    var contextUs = args.Length >= 6 ? long.Parse(args[5]) : 5_000;

    using var trace = TraceLog.OpenOrConvert(input);
    var interruptEvents = new List<InterruptEvent>();
    foreach (var ev in trace.Events)
    {
        if (!TryInterrupt(ev, out var us, out var hasStack))
            continue;

        interruptEvents.Add(new InterruptEvent(
            TimeUs: (long)(ev.TimeStampRelativeMSec * 1000),
            EventName: ev.EventName,
            Us: us,
            HasStack: hasStack));
    }

    var selected = interruptEvents
        .Where(e => e.HasStack)
        .OrderBy(e => e.Us)
        .Take(stackCount)
        .Concat(interruptEvents
            .Where(e => !e.HasStack)
            .OrderByDescending(e => e.Us)
            .Take(missingCount))
        .OrderBy(e => e.TimeUs)
        .ToList();

    if (selected.Count == 0)
    {
        Console.Error.WriteLine("no DPC/ISR events selected");
        return 1;
    }

    var selectedTimes = selected
        .GroupBy(e => e.TimeUs)
        .ToDictionary(g => g.Key, g => g.Count());

    var windows = selected
        .Select(e => (Start: e.TimeUs - contextUs, End: e.TimeUs + contextUs))
        .ToList();

    long kept = 0;
    long dropped = 0;
    using var relog = new ETWReloggerTraceEventSource(input, output);
    relog.AllEvents += data =>
    {
        var tsUs = (long)(data.TimeStampRelativeMSec * 1000);
        var isInterrupt = IsInterruptEventName(data.EventName);
        if (isInterrupt)
        {
            if (!selectedTimes.TryGetValue(tsUs, out var remaining) || remaining <= 0)
            {
                dropped++;
                return;
            }

            selectedTimes[tsUs] = remaining - 1;
            relog.WriteEvent(data);
            kept++;
            return;
        }

        if (windows.Any(window => tsUs >= window.Start && tsUs <= window.End))
        {
            relog.WriteEvent(data);
            kept++;
        }
        else
        {
            dropped++;
        }
    };
    relog.Process();

    var chosen = new Bucket();
    foreach (var e in selected)
        chosen.Add(e.Us, e.HasStack);

    Console.WriteLine(
        $"selected count={chosen.TotalCount} stackCount={chosen.StackCount} noStackCount={chosen.NoStackCount} " +
        $"totalUs={chosen.TotalUs} stackUs={chosen.StackUs} noStackUs={chosen.NoStackUs}");
    Console.WriteLine($"kept={kept} dropped={dropped} outBytes={new FileInfo(output).Length}");
    return 0;
}

static bool TryInterrupt(TraceEvent ev, out long us, out bool hasStack)
{
    switch (ev)
    {
        case DPCTraceData dpc:
            us = (long)(dpc.ElapsedTimeMSec * 1000);
            hasStack = dpc.CallStackIndex() != CallStackIndex.Invalid;
            return true;
        case ISRTraceData isr:
            us = (long)(isr.ElapsedTimeMSec * 1000);
            hasStack = isr.CallStackIndex() != CallStackIndex.Invalid;
            return true;
        default:
            us = 0;
            hasStack = false;
            return false;
    }
}

static bool IsInterruptEventName(string eventName)
    => eventName.Contains("DPC", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("ISR", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("Task(ce1dbfb4-137e-4da6-87b0-3f59aa102cbc)/Opcode(50)", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("Task(ce1dbfb4-137e-4da6-87b0-3f59aa102cbc)/Opcode(67)", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("Task(ce1dbfb4-137e-4da6-87b0-3f59aa102cbc)/Opcode(68)", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("Task(ce1dbfb4-137e-4da6-87b0-3f59aa102cbc)/Opcode(69)", StringComparison.OrdinalIgnoreCase) ||
       eventName.Contains("Task(ce1dbfb4-137e-4da6-87b0-3f59aa102cbc)/Opcode(96)", StringComparison.OrdinalIgnoreCase);

internal sealed record InterruptEvent(long TimeUs, string EventName, long Us, bool HasStack);

internal struct Bucket
{
    public long TotalCount;
    public long StackCount;
    public long NoStackCount;
    public long TotalUs;
    public long StackUs;
    public long NoStackUs;

    public void Add(long us, bool hasStack)
    {
        TotalCount++;
        TotalUs += us;
        if (hasStack)
        {
            StackCount++;
            StackUs += us;
        }
        else
        {
            NoStackCount++;
            NoStackUs += us;
        }
    }
}
