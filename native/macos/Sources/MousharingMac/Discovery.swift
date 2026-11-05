import Foundation
import Darwin

struct DiscoveredDevice:Identifiable { var id:String; var name:String; var host:String; var lastSeen:Date }
final class NativeDiscovery {
    private var descriptor:Int32 = -1
    private var source:DispatchSourceRead?
    private var timer:DispatchSourceTimer?
    private let queue=DispatchQueue(label:"com.widjis.mousharing.native.discovery")
    var onFound:((DiscoveredDevice)->Void)?
    func start(identity:AppSettings,advertise:Bool) {
        stop()
        let fd=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP); guard fd>=0 else { return }; descriptor=fd
        var one:Int32=1; setsockopt(fd,SOL_SOCKET,SO_REUSEADDR,&one,socklen_t(MemoryLayout.size(ofValue:one))); setsockopt(fd,SOL_SOCKET,SO_REUSEPORT,&one,socklen_t(MemoryLayout.size(ofValue:one))); setsockopt(fd,SOL_SOCKET,SO_BROADCAST,&one,socklen_t(MemoryLayout.size(ofValue:one)))
        var address=sockaddr_in(); address.sin_len=UInt8(MemoryLayout<sockaddr_in>.size);address.sin_family=sa_family_t(AF_INET);address.sin_port=discoveryPort.bigEndian;address.sin_addr=in_addr(s_addr:INADDR_ANY)
        let result=withUnsafePointer(to:&address) { $0.withMemoryRebound(to:sockaddr.self,capacity:1) { bind(fd,$0,socklen_t(MemoryLayout<sockaddr_in>.size)) } }; guard result==0 else { close(fd);descriptor = -1;return }
        _ = fcntl(fd,F_SETFL,O_NONBLOCK)
        func broadcast(_ object:[String:Any],destination:in_addr?=nil) { guard let data=try? jsonData(object),data.count<2048 else { return }; var to=sockaddr_in();to.sin_len=UInt8(MemoryLayout<sockaddr_in>.size);to.sin_family=sa_family_t(AF_INET);to.sin_port=discoveryPort.bigEndian;to.sin_addr=destination ?? in_addr(s_addr:INADDR_BROADCAST); data.withUnsafeBytes { bytes in withUnsafePointer(to:&to) { $0.withMemoryRebound(to:sockaddr.self,capacity:1) { _=sendto(fd,bytes.baseAddress,data.count,0,$0,socklen_t(MemoryLayout<sockaddr_in>.size)) } } } }
        let metadata:[String:Any]=["service":"mousharing-native","v":1,"id":identity.deviceID,"name":identity.deviceName,"port":nativePort]
        let read=DispatchSource.makeReadSource(fileDescriptor:fd,queue:queue);read.setEventHandler { [weak self] in
            var buffer=[UInt8](repeating:0,count:4096);var from=sockaddr_in();var length=socklen_t(MemoryLayout<sockaddr_in>.size)
            let count=withUnsafeMutablePointer(to:&from) { p in p.withMemoryRebound(to:sockaddr.self,capacity:1) { recvfrom(fd,&buffer,buffer.count,0,$0,&length) } }; guard count>0,count<=2048,let object=try? jsonObject(Data(buffer.prefix(count))),object["service"] as? String == "mousharing-native",object["v"] as? Int == 1,object["id"] as? String != identity.deviceID else { return }
            if object["query"] as? Bool == true { if advertise { broadcast(metadata,destination:from.sin_addr) }; return }
            guard let id=object["id"] as? String,UUID(uuidString:id) != nil,let name=object["name"] as? String,name.count<=128,object["port"] as? Int == Int(nativePort) else { return }
            var ip=[CChar](repeating:0,count:Int(INET_ADDRSTRLEN)); var raw=from.sin_addr; inet_ntop(AF_INET,&raw,&ip,socklen_t(ip.count)); self?.onFound?(DiscoveredDevice(id:id,name:name,host:String(cString:ip),lastSeen:Date()))
        };read.setCancelHandler { close(fd) };read.resume();source=read
        let timer=DispatchSource.makeTimerSource(queue:queue);timer.schedule(deadline:.now(),repeating:2);timer.setEventHandler { if advertise { broadcast(metadata) } else { broadcast(["service":"mousharing-native","v":1,"id":identity.deviceID,"query":true]) } };timer.resume();self.timer=timer
    }
    func stop() { timer?.cancel();timer=nil;if let source { source.cancel();self.source=nil;descriptor = -1 } else if descriptor>=0 { close(descriptor);descriptor = -1 } }
    deinit { stop() }
}
