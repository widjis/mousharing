import Foundation

// Lifecycle diagnostics only. Never pass payloads, pairing material or clipboard text.
enum NativeLog {
    private static let queue=DispatchQueue(label:"com.widjis.mousharing.diagnostics")
    static func event(_ event:String,_ detail:String="") {
        queue.async {
            do {
                let folder=FileManager.default.urls(for:.libraryDirectory,in:.userDomainMask)[0].appendingPathComponent("Logs/Mousharing",isDirectory:true)
                try FileManager.default.createDirectory(at:folder,withIntermediateDirectories:true,attributes:[.posixPermissions:0o700])
                let file=folder.appendingPathComponent("native.log")
                if let size=(try? FileManager.default.attributesOfItem(atPath:file.path)[.size]) as? NSNumber,size.intValue>1_048_576 {
                    let old=folder.appendingPathComponent("native.previous.log")
                    if FileManager.default.fileExists(atPath:old.path) { try FileManager.default.removeItem(at:old) }
                    try FileManager.default.moveItem(at:file,to:old)
                }
                if !FileManager.default.fileExists(atPath:file.path) { FileManager.default.createFile(atPath:file.path,contents:nil,attributes:[.posixPermissions:0o600]) }
                let line="\(ISO8601DateFormatter().string(from:Date())) \(event) \(detail.replacingOccurrences(of:"\n",with:" ").prefix(300))\n"
                let handle=try FileHandle(forWritingTo:file);defer { try? handle.close() };try handle.seekToEnd();try handle.write(contentsOf:Data(line.utf8))
            } catch { /* Diagnostics must never stop sharing. */ }
        }
    }
}
