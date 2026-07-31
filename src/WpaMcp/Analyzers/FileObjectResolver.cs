using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

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
// The plan template's FileIORundown / FileIOFileCreate handlers reading data.FileObject
// won't compile because FileIONameTraceData has no FileObject property. We therefore
// subscribe to FileIOCreate (the actual handle-allocating event) plus the operational
// events that observe a FileObject after open. This gives us a FileObject->FileName map
// for any file that experiences I/O during the trace, which matches what Tasks 12 and 13
// will analyze. We deliberately omit FileIONameTraceData rundown events: those expose
// only FileKey, not FileObject, and the public Resolve API takes a FileObject.
public sealed class FileObjectResolver
{
    private readonly Dictionary<ulong, string> _names = new();

    public static FileObjectResolver Build(TraceLog trace)
    {
        var resolver = new FileObjectResolver();

        // FileIOCreate is the handle-allocating event; the operational events (Read/Write/
        // Close/Cleanup/Flush/QueryInfo/SetInfo) all carry FileName + FileObject and let us
        // catch files opened before the trace started, the moment they first see I/O.
        void Capture(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOReadWriteTraceData d)
        {
            if (!string.IsNullOrEmpty(d.FileName))
                resolver._names[d.FileObject] = d.FileName;
        }
        void CaptureSimple(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOSimpleOpTraceData d)
        {
            if (!string.IsNullOrEmpty(d.FileName))
                resolver._names[d.FileObject] = d.FileName;
        }
        void CaptureInfo(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOInfoTraceData d)
        {
            if (!string.IsNullOrEmpty(d.FileName))
                resolver._names[d.FileObject] = d.FileName;
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIOCreate += d =>
            {
                if (!string.IsNullOrEmpty(d.FileName))
                    resolver._names[d.FileObject] = d.FileName;
            };
            kernel.FileIORead += Capture;
            kernel.FileIOWrite += Capture;
            kernel.FileIOClose += CaptureSimple;
            kernel.FileIOCleanup += CaptureSimple;
            kernel.FileIOFlush += CaptureSimple;
            kernel.FileIOQueryInfo += CaptureInfo;
            kernel.FileIOSetInfo += CaptureInfo;
        });
        return resolver;
    }

    public string Resolve(ulong fileObject)
        => _names.TryGetValue(fileObject, out var name) ? name : $"<unmapped:0x{fileObject:X}>";
}
