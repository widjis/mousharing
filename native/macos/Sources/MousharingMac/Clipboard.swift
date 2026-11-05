import AppKit
import CryptoKit

final class NativeClipboard {
    private let board=NSPasteboard.general
    private var revision:Int
    private var digest:Data?
    private var clock:Int64=0
    private var stamp:(Int64,String)=(0,"")
    private var pending:String?
    let origin:String
    init(origin:String) { self.origin=origin; revision=board.changeCount }
    func poll() -> [String:Any]? {
        guard flushPending() else { return nil }
        guard board.changeCount != revision else { return nil }; revision=board.changeCount
        guard let text=board.string(forType:.string) else { digest=nil; return nil }; let value=text.replacingOccurrences(of:"\r\n",with:"\n"); guard !value.contains("\0"),value.utf8.count<=maxClipboardBytes else { return nil }; let hash=Data(SHA256.hash(data:Data(value.utf8))); guard hash != digest else { return nil }; guard clock < Int64.max else { return nil };digest=hash; clock+=1; stamp=(clock,origin); return ["type":"clipboard","text":value,"clock":clock,"origin":origin]
    }
    private func flushPending() -> Bool { guard let value=pending else { return true };board.clearContents();guard board.setString(value,forType:.string) else { return false };revision=board.changeCount;digest=Data(SHA256.hash(data:Data(value.utf8)));pending=nil;return true }
    func apply(_ payload:[String:Any]) throws { guard let text=payload["text"] as? String,text.utf8.count<=maxClipboardBytes,!text.contains("\0"),let c=integer(payload["clock"]),c>0,let peer=payload["origin"] as? String,canonicalUUID(peer) else { throw NativeError.message("Clipboard tidak valid atau terlalu besar.") };clock=max(clock,c);let incoming=(c,peer);guard incoming.0>stamp.0 || incoming.0 == stamp.0 && incoming.1>stamp.1 else { return };pending=text.replacingOccurrences(of:"\r\n",with:"\n");stamp=incoming;_ = flushPending() }
}
