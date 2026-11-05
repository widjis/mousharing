import AppKit
import CoreGraphics

let macKeys: [CGKeyCode:String] = [0:"a",1:"s",2:"d",3:"f",4:"h",5:"g",6:"z",7:"x",8:"c",9:"v",11:"b",12:"q",13:"w",14:"e",15:"r",16:"y",17:"t",31:"o",32:"u",34:"i",35:"p",37:"l",38:"j",40:"k",45:"n",46:"m",18:"1",19:"2",20:"3",21:"4",23:"5",22:"6",26:"7",28:"8",25:"9",29:"0",24:"=",27:"-",30:"]",33:"[",39:"'",41:";",42:"\\",43:",",44:"/",47:".",50:"`",36:"enter",48:"tab",49:"space",51:"backspace",53:"esc",54:"cmd_r",55:"cmd",56:"shift",57:"caps_lock",58:"alt",59:"ctrl",60:"shift_r",61:"alt_r",62:"ctrl_r",123:"left",124:"right",125:"down",126:"up",117:"delete",115:"home",119:"end",116:"page_up",121:"page_down",122:"f1",120:"f2",99:"f3",118:"f4",96:"f5",97:"f6",98:"f7",100:"f8",101:"f9",109:"f10",103:"f11",111:"f12"]
let modifierFlags: [CGKeyCode:CGEventFlags] = [54:.maskCommand,55:.maskCommand,56:.maskShift,60:.maskShift,58:.maskAlternate,61:.maskAlternate,59:.maskControl,62:.maskControl,57:.maskAlphaShift]
final class InputEngine {
    var send:(([String:Any])->Void)?
    var failure:((String)->Void)?
    var localState:((Bool)->Void)?
    var role=DeviceRole.master
    var side=ScreenSide.right
    private var tap:CFMachPort?
    private var source:CFRunLoopSource?
    private var timer:Timer?
    private var screen:ScreenRect { let r=CGDisplayBounds(CGMainDisplayID()); return ScreenRect(x:r.origin.x,y:r.origin.y,width:r.width,height:r.height) }
    private var remote=false
    private var pending=false
    private var generation=0
    private var switchID=0
    private var entryRatio=0.5
    private var armedAt=Date.distantPast
    private var hidden=false
    private var detached=false
    private var previousApp:NSRunningApplication?
    private var savedWindows:[NSWindow]=[]
    private var physicalKeys=Set<CGKeyCode>()
    private var physicalButtons=Set<String>()
    private var physicalModifiers=Set<CGKeyCode>()
    private var capsOn=false
    private var heldKeys=Set<CGKeyCode>()
    private var heldButtons=Set<String>()
    private var receiverActive=false
    private var receiverSwitch=0
    private var receiverSide=ScreenSide.right
    private var returnPressure=0.0
    private var pendingDX=0.0, pendingDY=0.0
    private var receivedFlags=CGEventFlags()
    func start(role:DeviceRole,side:ScreenSide) throws {
        stop(); self.role=role; self.side=side;capsOn=CGEventSource.flagsState(.combinedSessionState).contains(.maskAlphaShift)
        guard role == .master else { return }
        let kinds:[CGEventType] = [.leftMouseDown,.leftMouseUp,.rightMouseDown,.rightMouseUp,.mouseMoved,.leftMouseDragged,.rightMouseDragged,.otherMouseDragged,.keyDown,.keyUp,.flagsChanged,.scrollWheel,.otherMouseDown,.otherMouseUp]
        let mask=kinds.reduce(CGEventMask(0)) { $0 | (CGEventMask(1)<<$1.rawValue) }
        guard let tap=CGEvent.tapCreate(tap:.cgSessionEventTap,place:.headInsertEventTap,options:.defaultTap,eventsOfInterest:mask,callback:{ _,type,event,info in guard let info else { return Unmanaged.passUnretained(event) }; return Unmanaged<InputEngine>.fromOpaque(info).takeUnretainedValue().handle(type,event) },userInfo:Unmanaged.passUnretained(self).toOpaque()) else { throw NativeError.message("Izinkan Accessibility dan Input Monitoring untuk Mousharing di Pengaturan Sistem, lalu coba lagi.") }
        self.tap=tap; let source=CFMachPortCreateRunLoopSource(nil,tap,0); self.source=source; CFRunLoopAddSource(CFRunLoopGetMain(),source,.commonModes); CGEvent.tapEnable(tap:tap,enable:true)
        let timer=Timer(timeInterval:1.0/120,repeats:true) { [weak self] _ in guard let self else { return }; if self.remote && !NSApplication.shared.isActive { self.fail("Capture foreground hilang. Sharing dijeda.");return };if self.remote, let tap=self.tap, !CGEvent.tapIsEnabled(tap:tap) { self.fail("Capture input Mac terhenti. Sharing dijeda.") }; self.flushMotion() }; RunLoop.main.add(timer,forMode:.common); self.timer=timer
    }
    func stop() { generation+=1; timer?.invalidate(); timer=nil; returnLocal(); releaseReceiver(); if let tap { CGEvent.tapEnable(tap:tap,enable:false); CFMachPortInvalidate(tap) }; if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(),source,.commonModes) }; tap=nil; source=nil; physicalKeys.removeAll(); physicalButtons.removeAll();physicalModifiers.removeAll() }
    private func fail(_ text:String) { send?(["type":"reset"]); returnLocal(); failure?(text) }
    private func flushMotion() { guard pendingDX != 0 || pendingDY != 0 else { return }; let x=pendingDX,y=pendingDY; pendingDX=0;pendingDY=0; if remote { send?(["type":"move","dx":x,"dy":y,"edgeSwitch":true]) } }
    private func suppress(_ event:CGEvent) -> Unmanaged<CGEvent>? { event.type = .null; return nil }
    private func handle(_ type:CGEventType,_ event:CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput { if remote || pending { fail("Capture input hilang. Hubungkan kembali.") }; return Unmanaged.passUnretained(event) }
        let key=CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        if (type == .keyDown || type == .keyUp) && (key == 53 || key == 100) {
            if type == .keyDown && event.getIntegerValueField(.keyboardEventAutorepeat) == 0 { if key == 53 || remote || pending { returnLocal() } else if event.flags.intersection([.maskCommand,.maskControl,.maskShift,.maskAlternate]).isEmpty { beginRemote(screen.ratio(side,event.location)) } }; return suppress(event)
        }
        var changedModifierDown:Bool?
        if type == .flagsChanged,key != 57,let flag=modifierFlags[key] { let down=event.flags.contains(flag) && !physicalModifiers.contains(key);changedModifierDown=down;if down { physicalModifiers.insert(key) } else { physicalModifiers.remove(key) } }
        if type == .keyDown { physicalKeys.insert(key) }; if type == .keyUp { physicalKeys.remove(key) }
        let button: String = (type == .leftMouseDown || type == .leftMouseUp) ? "left" : (type == .rightMouseDown || type == .rightMouseUp) ? "right":"middle"
        if [.leftMouseDown,.rightMouseDown,.otherMouseDown].contains(type) { physicalButtons.insert(button) }; if [.leftMouseUp,.rightMouseUp,.otherMouseUp].contains(type) { physicalButtons.remove(button) }
        if pending { return suppress(event) }
        if [.mouseMoved,.leftMouseDragged,.rightMouseDragged,.otherMouseDragged].contains(type) {
            let dx=event.getDoubleValueField(.mouseEventDeltaX),dy=event.getDoubleValueField(.mouseEventDeltaY)
            if remote { pendingDX=max(-2000,min(2000,pendingDX+dx)); pendingDY=max(-2000,min(2000,pendingDY+dy)); return suppress(event) }
            let modifiers:CGEventFlags=[.maskCommand,.maskControl,.maskShift,.maskAlternate]
            if Date()>armedAt && event.flags.intersection(modifiers).isEmpty && screen.crossed(side,event.location,dx,dy) { beginRemote(screen.ratio(side,event.location)); if pending { return suppress(event) } }
            return Unmanaged.passUnretained(event)
        }
        guard remote else { return Unmanaged.passUnretained(event) }; flushMotion()
        if type == .keyDown || type == .keyUp || type == .flagsChanged, let name=macKeys[key] {
            if name == "caps_lock" { send?(["type":"key","name":name,"down":true]); send?(["type":"key","name":name,"down":false]) }
            else { let down=type == .flagsChanged ? (changedModifierDown ?? false) : type == .keyDown; send?(["type":"key","name":name,"down":down]) }
        } else if [.leftMouseDown,.rightMouseDown,.otherMouseDown,.leftMouseUp,.rightMouseUp,.otherMouseUp].contains(type) { send?(["type":"button","name":button,"down":[.leftMouseDown,.rightMouseDown,.otherMouseDown].contains(type)]) }
        else if type == .scrollWheel { send?(["type":"scroll","dx":event.getIntegerValueField(.scrollWheelEventDeltaAxis2),"dy":event.getIntegerValueField(.scrollWheelEventDeltaAxis1)]) }
        return suppress(event)
    }
    private func beginRemote(_ ratio:Double) {
        guard !pending,!remote,physicalKeys.isEmpty,physicalButtons.isEmpty,physicalModifiers.isEmpty else { return }; pending=true; entryRatio=ratio; generation+=1; let current=generation; let deadline=Date().addingTimeInterval(1.5); previousApp=NSWorkspace.shared.frontmostApplication;savedWindows=NSApplication.shared.windows.filter { $0.isVisible && $0.level == .normal && $0.styleMask.contains(.titled) };for window in savedWindows { window.orderOut(nil) }; NSApplication.shared.activate(ignoringOtherApps:true)
        func attempt() { guard self.pending,self.generation == current else { return }; guard NSApplication.shared.isActive else { if Date()<deadline { DispatchQueue.main.asyncAfter(deadline:.now()+0.02,execute:attempt) } else { self.fail("Mac tidak dapat mengaktifkan capture kursor. Coba lagi.") }; return }
            guard CGDisplayHideCursor(CGMainDisplayID()) == .success else { self.fail("Kursor Mac tidak dapat disembunyikan."); return }; self.hidden=true
            guard CGAssociateMouseAndMouseCursorPosition(0) == .success else { self.fail("Gerakan kursor Mac tidak dapat ditahan."); return }; self.detached=true; self.pending=false; self.remote=true; self.switchID+=1; self.send?(["type":"enter","side":self.side.rawValue,"ratio":self.entryRatio,"switchId":self.switchID]); self.localState?(true)
        }; DispatchQueue.main.async(execute:attempt)
    }
    func returnLocal(ratio:Double?=nil) { let wasActive=remote || pending; generation+=1; remote=false; pending=false; pendingDX=0;pendingDY=0; if detached { CGAssociateMouseAndMouseCursorPosition(1); detached=false }; if hidden { CGDisplayShowCursor(CGMainDisplayID()); hidden=false }; if wasActive { send?(["type":"reset"]); CGWarpMouseCursorPosition(screen.point(side,ratio ?? entryRatio)); armedAt=Date().addingTimeInterval(0.3); localState?(false) }; let ownWasFront=previousApp?.processIdentifier == ProcessInfo.processInfo.processIdentifier;for window in savedWindows { if ownWasFront { window.makeKeyAndOrderFront(nil) } else { window.orderBack(nil) } };savedWindows=[];if let previousApp,NSApplication.shared.isActive { previousApp.activate(options:[]) }; previousApp=nil }
    func receive(_ payload:[String:Any]) throws {
        guard let type=payload["type"] as? String else { throw NativeError.message("Pesan input tidak valid.") }
        if role == .master { guard type == "leave",let id=integer(payload["switchId"]),id>0,let ratio=number(payload["ratio"]),(0...1).contains(ratio) else { throw NativeError.message("Pesan kembali ke master tidak valid.") };if id == Int64(switchID) { returnLocal(ratio:ratio) };return }
        if type == "reset" { releaseReceiver(); return }
        if type == "enter" { guard let raw=payload["side"] as? String,let side=ScreenSide(rawValue:raw),let ratio=number(payload["ratio"]),(0...1).contains(ratio),let id=integer(payload["switchId"]),id>0 else { throw NativeError.message("Posisi layar tidak valid.") }; releaseReceiver(); receiverSide=side; receiverSwitch=Int(id); receiverActive=true; CGWarpMouseCursorPosition(screen.point(side.opposite,ratio)); return }
        guard receiverActive else { return }
        if type == "move" { guard let dx=number(payload["dx"]),let dy=number(payload["dy"]),let edgeSwitch=boolean(payload["edgeSwitch"]) else { throw NativeError.message("Gerakan tidak valid.") }; let old=CGEvent(source:nil)?.location ?? CGPoint.zero; let p=screen.clamp(old.x+max(-2000,min(2000,dx)),old.y+max(-2000,min(2000,dy))); let mouseType:CGEventType=heldButtons.contains("left") ? .leftMouseDragged : heldButtons.contains("right") ? .rightMouseDragged:heldButtons.contains("middle") ? .otherMouseDragged:.mouseMoved; CGEvent(mouseEventSource:nil,mouseType:mouseType,mouseCursorPosition:p,mouseButton:.left)?.post(tap:.cghidEventTap)
            if edgeSwitch && heldKeys.isEmpty && heldButtons.isEmpty && screen.crossed(receiverSide.opposite,p,dx,dy) { returnPressure += abs(receiverSide == .left || receiverSide == .right ? dx:dy); if returnPressure>=24 { receiverActive=false; send?(["type":"leave","ratio":screen.ratio(receiverSide.opposite,p),"switchId":receiverSwitch]); releaseReceiver() } } else { returnPressure=0 }
        } else if type == "button" { guard let name=payload["name"] as? String,let down=boolean(payload["down"]),["left","right","middle"].contains(name) else { throw NativeError.message("Tombol mouse tidak valid.") }; if down { heldButtons.insert(name) } else { heldButtons.remove(name) }; postButton(name,down)
        } else if type == "key" { guard let name=payload["name"] as? String,let down=boolean(payload["down"]),let code=macKeys.first(where:{$0.value == name})?.key else { throw NativeError.message("Tombol keyboard tidak didukung.") }; if down { heldKeys.insert(code) } else { heldKeys.remove(code) }; postKey(code,down)
        } else if type == "scroll" { guard let dx=number(payload["dx"]),let dy=number(payload["dy"]) else { throw NativeError.message("Scroll tidak valid.") }; CGEvent(scrollWheelEvent2Source:nil,units:.line,wheelCount:2,wheel1:Int32(max(-100,min(100,dy))),wheel2:Int32(max(-100,min(100,dx))),wheel3:0)?.post(tap:.cghidEventTap) }
    }
    private func postKey(_ code:CGKeyCode,_ down:Bool) { if code == 57 && down { capsOn.toggle() };receivedFlags=capsOn ? [.maskAlphaShift]:[];for held in heldKeys where held != 57 { if let flag=modifierFlags[held] { receivedFlags.insert(flag) } };let event=CGEvent(keyboardEventSource:nil,virtualKey:code,keyDown:down);event?.flags=receivedFlags;event?.post(tap:.cghidEventTap) }
    private func postButton(_ name:String,_ down:Bool) { let button:CGMouseButton=name == "left" ? .left:name == "right" ? .right:.center; let type:CGEventType=name == "left" ? (down ? .leftMouseDown:.leftMouseUp):name == "right" ? (down ? .rightMouseDown:.rightMouseUp):(down ? .otherMouseDown:.otherMouseUp); CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:CGEvent(source:nil)?.location ?? .zero,mouseButton:button)?.post(tap:.cghidEventTap) }
    private func releaseReceiver() { for code in Array(heldKeys) { heldKeys.remove(code);postKey(code,false) };for button in heldButtons { postButton(button,false) }; heldKeys.removeAll();heldButtons.removeAll();receivedFlags=[];receiverActive=false;returnPressure=0 }
}
func number(_ raw:Any?) -> Double? { guard let n=raw as? NSNumber,CFGetTypeID(n) != CFBooleanGetTypeID(),n.doubleValue.isFinite else { return nil }; return n.doubleValue }
