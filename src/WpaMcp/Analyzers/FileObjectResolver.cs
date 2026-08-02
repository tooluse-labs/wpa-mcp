using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Joins FileObject (a kernel handle present in FileIO/PageFault events) to a file name.
//
// TraceEvent 3.2.2 reality vs the plan template:
//
//   The KernelTraceEventParser exposes these FileIO events that carry filenames:
//     * FileIOName, FileIOFileCreate, FileIOFileDelete, FileIOFileRundown
//         -> FileIONameTraceData (only FileKey + FileName, NO FileObject)
//     * FileIOCreate
//         -> FileIOCreateTraceData  (FileObject + FileName + ProcessID)
//     * FileIOCleanup, FileIOClose, FileIOFlush
//         -> FileIOSimpleOpTraceData (FileObject + FileName + FileKey)
//     * FileIORead, FileIOWrite
//         -> FileIOReadWriteTraceData (FileObject + FileName + FileKey)
//     * FileIOSetInfo, FileIODelete, FileIORename, FileIOQueryInfo, FileIOFSControl
//         -> FileIOInfoTraceData (FileObject + FileName + FileKey)
//     * FileIODirEnum, FileIODirNotify
//         -> FileIODirEnumTraceData (FileObject + FileName + FileKey)
//
// FileObject and FileKey values can be reused. Mappings are therefore observations on a
// timeline rather than one final dictionary value. Close and FileDelete observations end
// their respective lifetimes so a later reuse cannot rename earlier I/O.
public sealed class FileObjectResolver
{
    private readonly TemporalFileNameMap<ulong> _objectNames = new();
    private readonly TemporalFileNameMap<ulong> _keyNames = new();

    public static FileObjectResolver Build(TraceLog trace)
    {
        var resolver = new FileObjectResolver();
        KernelEventWalker.Walk(trace, resolver.Subscribe);
        return resolver;
    }

    internal void Subscribe(KernelTraceEventParser kernel)
    {
        // FileIOCreate is the handle-allocating event; the operational events (Read/Write/
        // Close/Cleanup/Flush/QueryInfo/SetInfo) all carry FileName + FileObject and let us
        // catch files opened before the trace started, the moment they first see I/O.
        void Capture(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOReadWriteTraceData d)
        {
            AddMapping(d.FileObject, d.FileKey, ToUs(d), d.EventIndex, d.FileName);
        }
        void CaptureSimple(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOSimpleOpTraceData d)
        {
            AddMapping(d.FileObject, d.FileKey, ToUs(d), d.EventIndex, d.FileName);
        }
        void CaptureInfo(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOInfoTraceData d)
        {
            AddMapping(d.FileObject, d.FileKey, ToUs(d), d.EventIndex, d.FileName);
        }
        void CaptureKey(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIONameTraceData d)
        {
            AddFileKeyMapping(d.FileKey, ToUs(d), d.EventIndex, d.FileName);
        }

        kernel.FileIOCreate += d =>
        {
            if (d.FileObject != 0 && !string.IsNullOrEmpty(d.FileName))
                _objectNames.Add(d.FileObject, ToUs(d), d.EventIndex, d.FileName);
        };
        kernel.FileIOName += CaptureKey;
        kernel.FileIOFileCreate += CaptureKey;
        kernel.FileIOFileDelete += d =>
        {
            CaptureKey(d);
            _keyNames.End(d.FileKey, ToUs(d), d.EventIndex);
        };
        kernel.FileIOFileRundown += CaptureKey;
        kernel.FileIORead += Capture;
        kernel.FileIOWrite += Capture;
        kernel.FileIOClose += d =>
        {
            CaptureSimple(d);
            EndFileObject(d.FileObject, ToUs(d), d.EventIndex);
        };
        kernel.FileIOCleanup += CaptureSimple;
        kernel.FileIOFlush += CaptureSimple;
        kernel.FileIOQueryInfo += CaptureInfo;
        kernel.FileIOSetInfo += CaptureInfo;
        kernel.FileIORename += CaptureInfo;
    }

    public string Resolve(ulong fileObject)
        => _objectNames.TryResolveLatest(fileObject, out var name)
            ? name
            : Unmapped(fileObject);

    internal string ResolveAt(ulong fileObject, ulong fileKey, long timestampUs)
        => ResolveDetailedAt(fileObject, fileKey, timestampUs).File;

    internal string ResolveAt(
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        EventIndex eventIndex)
        => ResolveDetailedAt(fileObject, fileKey, timestampUs, eventIndex).File;

    internal FileMappingResolution ResolveDetailedAt(
        ulong fileObject,
        ulong fileKey,
        long timestampUs)
    {
        string? keyName = null;
        string? objectName = null;
        if (fileKey != 0 && !_keyNames.TryResolveAt(fileKey, timestampUs, out keyName))
            keyName = null;
        if (fileObject != 0 && !_objectNames.TryResolveAt(fileObject, timestampUs, out objectName))
            objectName = null;
        return FileMappingResolution.FromTemporalMappings(
            fileObject,
            fileKey,
            keyName,
            objectName);
    }

    internal FileMappingResolution ResolveDetailedAt(
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        EventIndex eventIndex)
    {
        string? keyName = null;
        string? objectName = null;
        if (fileKey != 0 && !_keyNames.TryResolveAt(fileKey, timestampUs, eventIndex, out keyName))
            keyName = null;
        if (fileObject != 0 && !_objectNames.TryResolveAt(fileObject, timestampUs, eventIndex, out objectName))
            objectName = null;
        return FileMappingResolution.FromTemporalMappings(
            fileObject,
            fileKey,
            keyName,
            objectName);
    }

    internal void AddMapping(
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        if (fileObject != 0) _objectNames.Add(fileObject, timestampUs, fileName);
        if (fileKey != 0) _keyNames.Add(fileKey, timestampUs, fileName);
    }

    internal void AddFileKeyMapping(ulong fileKey, long timestampUs, string? fileName)
    {
        if (fileKey != 0 && !string.IsNullOrEmpty(fileName))
            _keyNames.Add(fileKey, timestampUs, fileName);
    }

    private void AddMapping(
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        EventIndex eventIndex,
        string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        if (fileObject != 0) _objectNames.Add(fileObject, timestampUs, eventIndex, fileName);
        if (fileKey != 0) _keyNames.Add(fileKey, timestampUs, eventIndex, fileName);
    }

    private void AddFileKeyMapping(
        ulong fileKey,
        long timestampUs,
        EventIndex eventIndex,
        string? fileName)
    {
        if (fileKey != 0 && !string.IsNullOrEmpty(fileName))
            _keyNames.Add(fileKey, timestampUs, eventIndex, fileName);
    }

    internal void EndFileObject(ulong fileObject, long timestampUs) =>
        EndFileObjectIfPresent(fileObject, timestampUs);

    private void EndFileObject(ulong fileObject, long timestampUs, EventIndex eventIndex)
    {
        if (fileObject != 0) _objectNames.End(fileObject, timestampUs, eventIndex);
    }

    private void EndFileObjectIfPresent(ulong fileObject, long timestampUs)
    {
        if (fileObject != 0) _objectNames.End(fileObject, timestampUs);
    }

    private static long ToUs(Microsoft.Diagnostics.Tracing.TraceEvent data) =>
        TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);

    private static string Unmapped(ulong value) => FileIdentifierFormatting.Unmapped(value);
}

internal static class FileIdentifierFormatting
{
    public static string Pointer(ulong value) => $"0x{value:x16}";

    public static string Unmapped(ulong value) => $"<unmapped:{Pointer(value)}>";
}

internal static class FileMappingStates
{
    public const string EventName = "event_name";
    public const string TemporalFileKey = "temporal_file_key";
    public const string TemporalFileObject = "temporal_file_object";
    public const string AmbiguousTemporalMapping = "ambiguous_temporal_mapping";
    public const string UnresolvedFileIdentity = "unresolved_file_identity";
    public const string Mixed = "mixed";
}

internal readonly record struct FileMappingResolution(string File, string MappingState)
{
    public static FileMappingResolution FromEventName(string fileName) =>
        new(fileName, FileMappingStates.EventName);

    public static FileMappingResolution FromFileKey(
        ulong fileKey,
        string? fileName) =>
        !string.IsNullOrEmpty(fileName)
            ? new(fileName, FileMappingStates.TemporalFileKey)
            : Unresolved(fileKey);

    public static FileMappingResolution FromTemporalMappings(
        ulong fileObject,
        ulong fileKey,
        string? fileKeyName,
        string? fileObjectName)
    {
        if (fileKeyName is not null && fileObjectName is not null)
        {
            if (!string.Equals(fileKeyName, fileObjectName, StringComparison.OrdinalIgnoreCase))
            {
                return new FileMappingResolution(
                    $"<ambiguous:file-key={FileIdentifierFormatting.Pointer(fileKey)};file-object={FileIdentifierFormatting.Pointer(fileObject)}>",
                    FileMappingStates.AmbiguousTemporalMapping);
            }

            return new FileMappingResolution(fileKeyName, FileMappingStates.TemporalFileKey);
        }

        if (fileKeyName is not null)
            return new FileMappingResolution(fileKeyName, FileMappingStates.TemporalFileKey);
        if (fileObjectName is not null)
            return new FileMappingResolution(fileObjectName, FileMappingStates.TemporalFileObject);
        return Unresolved(fileObject != 0 ? fileObject : fileKey);
    }

    private static FileMappingResolution Unresolved(ulong identifier) =>
        new(FileIdentifierFormatting.Unmapped(identifier), FileMappingStates.UnresolvedFileIdentity);
}

internal sealed class FileMappingStateAccumulator
{
    private long _eventNameEventCount;
    private long _temporalFileKeyEventCount;
    private long _temporalFileObjectEventCount;
    private long _ambiguousTemporalMappingEventCount;
    private long _unresolvedFileIdentityEventCount;

    public void Add(string mappingState)
    {
        checked
        {
            switch (mappingState)
            {
                case FileMappingStates.EventName:
                    _eventNameEventCount++;
                    break;
                case FileMappingStates.TemporalFileKey:
                    _temporalFileKeyEventCount++;
                    break;
                case FileMappingStates.TemporalFileObject:
                    _temporalFileObjectEventCount++;
                    break;
                case FileMappingStates.AmbiguousTemporalMapping:
                    _ambiguousTemporalMappingEventCount++;
                    break;
                case FileMappingStates.UnresolvedFileIdentity:
                    _unresolvedFileIdentityEventCount++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mappingState),
                        mappingState,
                        "Unknown file mapping state.");
            }
        }
    }

    public FileMappingStateCounts Snapshot() => new(
        _eventNameEventCount,
        _temporalFileKeyEventCount,
        _temporalFileObjectEventCount,
        _ambiguousTemporalMappingEventCount,
        _unresolvedFileIdentityEventCount);

    public string AggregateState
    {
        get
        {
            var populatedStateCount = 0;
            string? onlyState = null;
            Observe(_eventNameEventCount, FileMappingStates.EventName);
            Observe(_temporalFileKeyEventCount, FileMappingStates.TemporalFileKey);
            Observe(_temporalFileObjectEventCount, FileMappingStates.TemporalFileObject);
            Observe(_ambiguousTemporalMappingEventCount, FileMappingStates.AmbiguousTemporalMapping);
            Observe(_unresolvedFileIdentityEventCount, FileMappingStates.UnresolvedFileIdentity);

            if (populatedStateCount == 0)
                throw new InvalidOperationException("Cannot summarize an empty mapping-state accumulator.");
            return populatedStateCount == 1 ? onlyState! : FileMappingStates.Mixed;

            void Observe(long count, string state)
            {
                if (count == 0) return;
                populatedStateCount++;
                onlyState = state;
            }
        }
    }
}

internal sealed class TemporalFileNameMap<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, List<Entry>> _entries = new();
    private long _nextEventOrder;

    public void Add(TKey key, long timestampUs, string fileName) =>
        Add(key, timestampUs, NextEventOrder(), fileName);

    public void Add(TKey key, long timestampUs, long eventOrder, string fileName) =>
        AddEntry(key, new Entry(timestampUs, OrderKey.FromSynthetic(eventOrder), fileName));

    public void Add(TKey key, long timestampUs, EventIndex eventIndex, string fileName) =>
        AddEntry(key, new Entry(timestampUs, OrderKey.FromTrace(eventIndex), fileName));

    public void End(TKey key, long timestampUs) =>
        End(key, timestampUs, NextEventOrder());

    public void End(TKey key, long timestampUs, long eventOrder) =>
        AddEntry(key, new Entry(timestampUs, OrderKey.FromSynthetic(eventOrder), FileName: null));

    public void End(TKey key, long timestampUs, EventIndex eventIndex) =>
        AddEntry(key, new Entry(timestampUs, OrderKey.FromTrace(eventIndex), FileName: null));

    private void AddEntry(TKey key, Entry entry)
    {
        if (!_entries.TryGetValue(key, out var entries))
        {
            entries = new List<Entry>();
            _entries.Add(key, entries);
        }

        var insertAt = UpperBound(entries, entry.TimestampUs, entry.Order);
        if (insertAt > 0 && entries[insertAt - 1].FileName == entry.FileName)
            return;
        entries.Insert(insertAt, entry);
    }

    public bool TryResolveAt(TKey key, long timestampUs, out string fileName)
    {
        if (_entries.TryGetValue(key, out var entries))
        {
            var index = UpperBoundTimestamp(entries, timestampUs) - 1;
            if (index >= 0 && entries[index].FileName is { } resolved)
            {
                fileName = resolved;
                return true;
            }
        }

        fileName = string.Empty;
        return false;
    }

    public bool TryResolveAt(TKey key, long timestampUs, long eventOrder, out string fileName)
        => TryResolveAt(key, timestampUs, OrderKey.FromSynthetic(eventOrder), out fileName);

    public bool TryResolveAt(
        TKey key,
        long timestampUs,
        EventIndex eventIndex,
        out string fileName)
        => TryResolveAt(key, timestampUs, OrderKey.FromTrace(eventIndex), out fileName);

    private bool TryResolveAt(TKey key, long timestampUs, OrderKey order, out string fileName)
    {
        if (_entries.TryGetValue(key, out var entries))
        {
            var index = UpperBound(entries, timestampUs, order) - 1;
            if (index >= 0 && entries[index].FileName is { } resolved)
            {
                fileName = resolved;
                return true;
            }
        }

        fileName = string.Empty;
        return false;
    }

    public bool TryResolveLatest(TKey key, out string fileName)
    {
        if (_entries.TryGetValue(key, out var entries) &&
            entries.Count > 0 &&
            entries[^1].FileName is { } resolved)
        {
            fileName = resolved;
            return true;
        }

        fileName = string.Empty;
        return false;
    }

    private long NextEventOrder() => ++_nextEventOrder;

    private static int UpperBound(List<Entry> entries, long timestampUs, OrderKey order)
    {
        var low = 0;
        var high = entries.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var entry = entries[middle];
            if (entry.TimestampUs < timestampUs ||
                (entry.TimestampUs == timestampUs && entry.Order.CompareTo(order) <= 0))
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int UpperBoundTimestamp(List<Entry> entries, long timestampUs)
    {
        var low = 0;
        var high = entries.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (entries[middle].TimestampUs <= timestampUs)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private readonly record struct Entry(long TimestampUs, OrderKey Order, string? FileName);

    private readonly record struct OrderKey(bool IsTraceEvent, EventIndex TraceEvent, long Synthetic)
    {
        public static OrderKey FromTrace(EventIndex eventIndex) =>
            new(IsTraceEvent: true, eventIndex, Synthetic: 0);

        public static OrderKey FromSynthetic(long eventOrder) =>
            new(IsTraceEvent: false, EventIndex.Invalid, eventOrder);

        public int CompareTo(OrderKey other)
        {
            if (IsTraceEvent && other.IsTraceEvent)
                return TraceEvent.CompareTo(other.TraceEvent);
            if (!IsTraceEvent && !other.IsTraceEvent)
                return Synthetic.CompareTo(other.Synthetic);
            return IsTraceEvent ? 1 : -1;
        }
    }
}
