using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace WprMcp.Analyzers;

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
        var kernel = new KernelTraceEventParser(trace);

        // FileIOCreate fires when a kernel handle (FileObject) is allocated for a file.
        kernel.FileIOCreate += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };

        // Operational events observe an existing FileObject and re-emit its filename.
        // Subscribing here catches files opened before the trace started, the moment
        // they see their first I/O.
        kernel.FileIORead += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOWrite += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOClose += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOCleanup += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOFlush += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOQueryInfo += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };
        kernel.FileIOSetInfo += data =>
        {
            if (!string.IsNullOrEmpty(data.FileName))
                resolver._names[data.FileObject] = data.FileName;
        };

        // Walk the entire trace once to populate the map.
        trace.Events.GetSource().Process();
        return resolver;
    }

    public string Resolve(ulong fileObject)
        => _names.TryGetValue(fileObject, out var name) ? name : $"<unmapped:0x{fileObject:X}>";
}
