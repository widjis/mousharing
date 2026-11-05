"""Mousharing experimental Mac sender / Windows receiver. Python 3.9+."""
import argparse
import getpass
import hashlib
from pathlib import Path
import json
import queue
import secrets
import socket
import ssl
import sys
import threading
import time

from screens import Screen, SIDES, OPPOSITE, fraction, windows_primary_screen, ReturnGate

from input_transport import take_input
from cursor_capture import MacCursorCapture
from clipboard_sync import ClipboardSync, make_clipboard, checked_event, MAX_TEXT_BYTES, POLL_SECONDS

PORT = 47831
PROTOCOL_VERSION = 3
MAX_FRAME_BYTES = 6*MAX_TEXT_BYTES+4096
# Use physical Windows virtual keys so Ctrl+C/V works as keyboard shortcuts.
WINDOWS_VK = {c: ord(c.upper()) for c in 'abcdefghijklmnopqrstuvwxyz0123456789'}
WINDOWS_VK.update({';':0xBA, '=':0xBB, ',':0xBC, '-':0xBD, '.':0xBE,
                   '/':0xBF, '`':0xC0, '[':0xDB, '\\':0xDC, ']':0xDD, "'":0xDE})
# Physical ANSI US key positions; layout translation is intentionally not attempted.
CODES = {}
CODES.update(dict(zip([0,1,2,3,4,5,6,7,8,9,11,12,13,14,15,16,17,31,32,34,35,37,38,40,45,46], 'asdfhgzxcvbqwerytouipljknm')))
CODES.update({18:'1',19:'2',20:'3',21:'4',23:'5',22:'6',26:'7',28:'8',25:'9',29:'0',24:'=',27:'-',30:']',33:'[',39:"'",41:';',42:'\\',43:',',44:'/',47:'.',50:'`',36:'enter',48:'tab',49:'space',51:'backspace',53:'esc',54:'cmd_r',55:'cmd',56:'shift',57:'caps_lock',58:'alt',59:'ctrl',60:'shift_r',61:'alt_r',62:'ctrl_r',123:'left',124:'right',125:'down',126:'up',117:'delete',115:'home',119:'end',116:'page_up',121:'page_down'})
CODES.update({122:'f1',120:'f2',99:'f3',118:'f4',96:'f5',97:'f6',98:'f7',100:'f8',101:'f9',109:'f10',103:'f11',111:'f12'})


def send(sock, event):
    frame=(json.dumps(event, ensure_ascii=False, separators=(',', ':'))+'\n').encode('utf-8')
    if len(frame) > MAX_FRAME_BYTES:
        raise ValueError('Frame exceeds size limit')
    sock.sendall(frame)


def messages(sock):
    with sock.makefile('rb') as stream:
        while True:
            line = stream.readline(MAX_FRAME_BYTES+1)
            if not line:
                return
            if len(line) > MAX_FRAME_BYTES or not line.endswith(b'\n'):
                raise ValueError('Invalid frame')
            event = json.loads(line)
            if not isinstance(event, dict):
                raise ValueError('Expected object')
            if event.get('type') == 'clipboard':
                checked_event(event)
            elif len(line) > 4096:
                raise ValueError('Input frame exceeds size limit')
            yield event


def receive(args):
    if sys.platform != 'win32':
        raise SystemExit('Receiver runs on Windows.')
    screen = windows_primary_screen()
    from pynput import keyboard, mouse
    kb, pointer = keyboard.Controller(), mouse.Controller()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(args.cert, args.key)
    token = getpass.getpass('Pairing password (same on Mac, at least 16 characters): ')
    if len(token) < 16:
        raise SystemExit('Password needs at least 16 characters.')
    with socket.create_server((args.bind, args.port)) as server:
        print('Waiting for Mac. Ctrl+C stops receiver.', flush=True)
        while True:
            raw, address = server.accept()
            handle_connection(raw, context, token, kb, pointer, keyboard, mouse, screen,
                              clipboard_factory=make_clipboard,clipboard_enabled=not args.no_clipboard)


def handle_connection(raw, context, token, kb, pointer, keyboard, mouse, screen,
                      clipboard_factory=None,clipboard_enabled=True):
    """One authenticated session; always release pressed inputs on exit."""
    clipboard=None
    clip_stop=threading.Event()
    clip_thread=None
    held_keys, held_buttons = set(), set()
    active = False
    return_gate = ReturnGate()
    side, switch_id = 'right', 0
    def release_inputs():
        for key in list(held_keys):
            kb.release(key)
            held_keys.discard(key)
        for button in list(held_buttons):
            pointer.release(button)
            held_buttons.discard(button)
    try:
        raw.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
        raw.settimeout(5)
        with context.wrap_socket(raw, server_side=True) as conn:
            events = messages(conn)
            hello = next(events)
            if hello.get('version') != PROTOCOL_VERSION:
                send(conn,{'ready':False,'error':'protocol',
                           'message':'Protocol mismatch. Update and restart both laptops.'})
                raise ValueError('Protocol mismatch')
            if not secrets.compare_digest(str(hello.get('token','')).encode('utf-8'),token.encode('utf-8')):
                send(conn,{'ready':False,'error':'pairing',
                           'message':'Pairing password does not match the Windows receiver.'})
                raise ValueError('Pairing rejected')
            send_lock=threading.Lock()
            def reply(event):
                with send_lock:
                    send(conn,event)
            if clipboard_enabled and hello.get('clipboard') is True and clipboard_factory:
                try:
                    clipboard=ClipboardSync(clipboard_factory())
                except (OSError,ValueError):
                    print('Clipboard unavailable; input sharing remains active.',flush=True)
            reply({'ready':True,'screen':{'width':screen.width,'height':screen.height},
                   'clipboard':clipboard is not None})
            if clipboard is not None:
                def poll_clipboard():
                    try:
                        while not clip_stop.wait(POLL_SECONDS):
                            event=clipboard.poll()
                            if event is not None:
                                reply(event)
                    except OSError:
                        try:
                            conn.shutdown(socket.SHUT_RDWR)
                        except OSError:
                            pass
                clip_thread=threading.Thread(target=poll_clipboard,daemon=True)
                clip_thread.start()
            print('Mac connected.', flush=True)
            for event in events:
                kind = event.get('type')
                if kind == 'ping':
                    reply({'type':'pong'})
                    continue
                if kind == 'clipboard':
                    if clipboard is not None:
                        clipboard.apply_event(event)
                    continue
                if kind == 'reset':
                    return_gate.reset()
                    release_inputs()
                    active = False
                elif kind == 'enter':
                    return_gate.reset()
                    release_inputs()
                    side = event['side']
                    if side not in SIDES:
                        raise ValueError('Invalid screen side')
                    switch_id = int(event['switch_id'])
                    pointer.position = tuple(round(v) for v in screen.point(OPPOSITE[side],event['ratio']))
                    active = True
                elif not active:
                    # Ignore input already queued when this receiver returned control to Mac.
                    continue
                elif kind == 'move':
                    dx=max(-2000,min(2000,int(event['dx'])))
                    dy=max(-2000,min(2000,int(event['dy'])))
                    x,y=pointer.position
                    x,y=screen.clamp(x+dx,y+dy)
                    pointer.position=(round(x),round(y))
                    if event.get('edge_switch') and return_gate.should_leave(screen,OPPOSITE[side],x,y,dx,dy,blocked=bool(held_keys or held_buttons)):
                        active=False
                        release_inputs()
                        reply({'type':'leave','switch_id':switch_id,
                                    'ratio':screen.ratio(OPPOSITE[side],x,y)})
                elif kind == 'scroll':
                    pointer.scroll(max(-100,min(100,int(event['dx']))), max(-100,min(100,int(event['dy']))))
                elif kind == 'button':
                    button = getattr(mouse.Button, event['name'])
                    if event['down']:
                        held_buttons.add(button)
                        pointer.press(button)
                    else:
                        pointer.release(button)
                        held_buttons.discard(button)
                elif kind == 'key':
                    name = event['name']
                    if name not in CODES.values():
                        raise ValueError('Unsupported key')
                    key = keyboard.KeyCode.from_vk(WINDOWS_VK[name]) if name in WINDOWS_VK else getattr(keyboard.Key,name)
                    if event['down']:
                        held_keys.add(key)
                        kb.press(key)
                    else:
                        kb.release(key)
                        held_keys.discard(key)
    except (OSError, ValueError, TypeError, KeyError, AttributeError, StopIteration) as exc:
        print('Connection ended:', type(exc).__name__, flush=True)
    finally:
        clip_stop.set()
        if clip_thread is not None:
            clip_thread.join(timeout=1)
        if clipboard is not None:
            clipboard.close()
        raw.close()
        release_inputs()


def transmit(args):
    if sys.platform != 'darwin':
        raise SystemExit('Sender runs on macOS.')
    import Quartz as Q
    display=Q.CGMainDisplayID()
    bounds=Q.CGDisplayBounds(display)
    try:
        screen=Screen(bounds.origin.x,bounds.origin.y,bounds.size.width,bounds.size.height)
    except ValueError:
        raise SystemExit('No Mac primary display. Run from Terminal in your logged-in desktop session.')
    token = getpass.getpass('Pairing password: ')
    context = ssl.create_default_context(cafile=args.cert)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    # Trust is bound to the supplied certificate, not the LAN address.
    context.check_hostname = False
    raw = socket.create_connection((args.host,args.port),timeout=5)
    raw.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
    conn = context.wrap_socket(raw, server_hostname=args.host)
    send(conn, {'token':token, 'version':PROTOCOL_VERSION,'clipboard':not getattr(args,'no_clipboard',False)})
    incoming = messages(conn)
    try:
        ready=next(incoming)
    except StopIteration:
        conn.close()
        raise SystemExit('Receiver closed pairing. Update and restart the Windows receiver, then check that both pairing passwords match.') from None
    if ready.get('ready') is not True:
        conn.close()
        raise SystemExit(ready.get('message','Receiver rejected connection'))
    clipboard=None
    if ready.get('clipboard') is True and not getattr(args,'no_clipboard',False):
        clipboard=ClipboardSync(make_clipboard())
    print('Clipboard text sharing: '+('enabled' if clipboard is not None else 'disabled'),flush=True)
    peer=ready['screen']
    Screen(0,0,float(peer['width']),float(peer['height']))
    print('Windows primary screen: {} x {}; Windows is {} of Mac.'.format(peer['width'],peer['height'],args.windows_side),flush=True)
    events = queue.Queue(maxsize=2048)
    controls = queue.Queue(maxsize=64)
    state = {'remote':False, 'alive':True, 'switch_id':0, 'ratio':0.5,
             'pending':False, 'capture_deadline':0.0, 'armed_at':0.0}
    cursor=MacCursorCapture(Q)
    physical_keys, physical_buttons = set(),set()
    def enqueue(event):
        try:
            events.put_nowait(event)
        except queue.Full:
            state['alive']=False
    def disconnect():
        state['alive']=False
        try:
            conn.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        conn.close()
    def go_local(ratio=None):
        was_remote=state['remote']
        was_pending=state['pending']
        state.update(remote=False,pending=False)
        cursor.release(display)
        if was_pending:
            state['armed_at']=time.monotonic()+0.25
        if was_remote:
            ratio=state['ratio'] if ratio is None else fraction(ratio)
            Q.CGWarpMouseCursorPosition(screen.point(args.windows_side,ratio))
            state['armed_at']=time.monotonic()+0.25
            enqueue({'type':'reset'})
            print('Control: Mac',flush=True)
    def go_remote(ratio):
        if not state['alive'] or state['pending'] or physical_keys or physical_buttons:
            return False
        state.update(pending=True,ratio=fraction(ratio),capture_deadline=time.monotonic()+1.5)
        cursor.request()
        return True
    def complete_capture():
        try:
            acquired=cursor.acquire(display)
        except OSError as exc:
            print(str(exc)+'. Staying on Mac.',flush=True)
            go_local()
            return
        if acquired is None:
            if time.monotonic() >= state['capture_deadline']:
                print('Mac foreground activation timed out. Staying on Mac.',flush=True)
                go_local()
            return
        if clipboard is not None:
            update=clipboard.poll()
            if update is not None:
                enqueue(update)
        state.update(pending=False,remote=True,switch_id=state['switch_id']+1)
        enqueue({'type':'enter','side':args.windows_side,'ratio':state['ratio'],
                 'switch_id':state['switch_id']})
        print('Control: Windows (Mac cursor captured)',flush=True)
    def monitor():
        try:
            for reply in incoming:
                if reply.get('type') in ('leave','clipboard'):
                    controls.put_nowait(reply)
                elif reply.get('type') != 'pong':
                    raise ValueError('Unexpected receiver response')
        except (OSError, ValueError, queue.Full):
            pass
        finally:
            incoming.close()
            disconnect()
    def worker():
        try:
            last_ping = time.monotonic()
            pending = None
            while state['alive']:
                if time.monotonic() - last_ping >= 1:
                    send(conn, {'type':'ping'})
                    last_ping = time.monotonic()
                try:
                    event,pending = take_input(events,pending)
                except queue.Empty:
                    continue
                send(conn,event)
        except OSError:
            pass
        finally:
            disconnect()
            print('Disconnected; returning control to Mac.',flush=True)
    threading.Thread(target=worker,daemon=True).start()
    threading.Thread(target=monitor,daemon=True).start()
    modifiers = {54:Q.kCGEventFlagMaskCommand,55:Q.kCGEventFlagMaskCommand,56:Q.kCGEventFlagMaskShift,60:Q.kCGEventFlagMaskShift,58:Q.kCGEventFlagMaskAlternate,61:Q.kCGEventFlagMaskAlternate,59:Q.kCGEventFlagMaskControl,62:Q.kCGEventFlagMaskControl,57:Q.kCGEventFlagMaskAlphaShift}
    modifier_mask=(Q.kCGEventFlagMaskCommand | Q.kCGEventFlagMaskShift |
                   Q.kCGEventFlagMaskAlternate | Q.kCGEventFlagMaskControl)
    def suppress(event):
        # Null the event as well as returning NULL from the tap callback.
        Q.CGEventSetType(event,Q.kCGEventNull)
        return None
    def callback(proxy,kind,event,ref):
        if kind in (Q.kCGEventTapDisabledByTimeout,Q.kCGEventTapDisabledByUserInput):
            if state['remote']:
                print('Input tap disabled; stopping remote session.',flush=True)
                disconnect()
            go_local()
            enqueue({'type':'reset'})
            Q.CGEventTapEnable(tap,True)
            return event
        code = int(Q.CGEventGetIntegerValueField(event,Q.kCGKeyboardEventKeycode)) if kind in (10,11,12) else -1
        if code in (100,53) and kind in (10,11):
            if kind == 10 and not Q.CGEventGetIntegerValueField(event,Q.kCGKeyboardEventAutorepeat):
                if code == 53 or state['remote'] or state['pending']:
                    go_local()
                else:
                    location=Q.CGEventGetLocation(event)
                    go_remote(screen.ratio(args.windows_side,location.x,location.y))
            return suppress(event)
        if kind == 10:
            physical_keys.add(code)
        elif kind == 11:
            physical_keys.discard(code)
        if kind in (1,3,25):
            physical_buttons.add(kind)
        elif kind in (2,4,26):
            physical_buttons.discard(kind-1)
        if not state['alive']:
            go_local()
            return event
        if state['pending']:
            return suppress(event)
        if kind in (5,6,7,27):
            dx=Q.CGEventGetIntegerValueField(event,Q.kCGMouseEventDeltaX)
            dy=Q.CGEventGetIntegerValueField(event,Q.kCGMouseEventDeltaY)
            if not state['remote']:
                location=Q.CGEventGetLocation(event)
                if (not args.no_edge_switch and time.monotonic() >= state['armed_at'] and
                        not Q.CGEventGetFlags(event) & modifier_mask and
                        screen.crossed(args.windows_side,location.x,location.y,dx,dy)):
                    if go_remote(screen.ratio(args.windows_side,location.x,location.y)):
                        return suppress(event)
                return event
            enqueue({'type':'move','dx':dx,'dy':dy,'edge_switch':not args.no_edge_switch})
            return suppress(event)
        if not state['remote']:
            return event
        if kind in (10,11,12):
            name = CODES.get(code)
            if name == 'caps_lock':
                enqueue({'type':'key','name':name,'down':True})
                enqueue({'type':'key','name':name,'down':False})
            elif name:
                down = kind == 10 if kind != 12 else bool(Q.CGEventGetFlags(event) & modifiers.get(code,0))
                enqueue({'type':'key','name':name,'down':down})
        elif kind in (1,2,3,4,25,26):
            name = 'left' if kind in (1,2) else 'right' if kind in (3,4) else 'middle'
            enqueue({'type':'button','name':name,'down':kind in (1,3,25)})
        elif kind == 22:
            enqueue({'type':'scroll','dx':Q.CGEventGetIntegerValueField(event,Q.kCGScrollWheelEventDeltaAxis2),'dy':Q.CGEventGetIntegerValueField(event,Q.kCGScrollWheelEventDeltaAxis1)})
        return suppress(event)
    mask = sum(1 << n for n in (1,2,3,4,5,6,7,10,11,12,22,25,26,27))
    tap = Q.CGEventTapCreate(Q.kCGSessionEventTap,Q.kCGHeadInsertEventTap,Q.kCGEventTapOptionDefault,mask,callback,None)
    if tap is None:
        disconnect()
        raise SystemExit('Allow Accessibility and Input Monitoring for your terminal, then restart.')
    source = Q.CFMachPortCreateRunLoopSource(None,tap,0)
    Q.CFRunLoopAddSource(Q.CFRunLoopGetCurrent(),source,Q.kCFRunLoopCommonModes)
    Q.CGEventTapEnable(tap,True)
    print('Connected. Move across the shared edge, or use F8. Escape returns to Mac.',flush=True)
    next_clipboard_poll=time.monotonic()+POLL_SECONDS
    try:
        while state['alive']:
            cursor.pump()
            if state['pending']:
                complete_capture()
            if state['remote'] and not cursor.is_active():
                print('Mac foreground capture lost; stopping remote session.',flush=True)
                disconnect()
                break
            if state['remote'] and not Q.CGEventTapIsEnabled(tap):
                print('Input capture lost; stopping remote session.',flush=True)
                disconnect()
                break
            while True:
                try:
                    reply=controls.get_nowait()
                except queue.Empty:
                    break
                if reply.get('type') == 'clipboard':
                    if clipboard is not None:
                        clipboard.apply_event(reply)
                    continue
                if state['remote'] and reply.get('switch_id') == state['switch_id']:
                    go_local(reply['ratio'])
            if clipboard is not None and time.monotonic() >= next_clipboard_poll:
                update=clipboard.poll()
                if update is not None:
                    enqueue(update)
                next_clipboard_poll=time.monotonic()+POLL_SECONDS
            Q.CFRunLoopRunInMode(Q.kCFRunLoopDefaultMode,0.02,False)
    finally:
        go_local()
        Q.CGEventTapEnable(tap,False)
        disconnect()
        if clipboard is not None:
            clipboard.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='role',required=True)
    fingerprint = sub.add_parser('fingerprint', help='Show SHA256 of the receiver certificate')
    fingerprint.add_argument('--cert',required=True)
    receiver = sub.add_parser('receive')
    receiver.add_argument('--bind',default='0.0.0.0')
    receiver.add_argument('--key',required=True)
    sender = sub.add_parser('send')
    sender.add_argument('--host',required=True)
    sender.add_argument('--windows-side',choices=SIDES,default='right',help='Windows position relative to Mac (default: right)')
    sender.add_argument('--no-edge-switch',action='store_true',help='Switch only with F8/Escape')
    for command in (receiver,sender):
        command.add_argument('--cert',required=True)
        command.add_argument('--port',type=int,default=PORT)
        command.add_argument('--no-clipboard',action='store_true',help='Disable clipboard sharing on this device')
    args = parser.parse_args()
    try:
        if args.role == 'fingerprint':
            der = ssl.PEM_cert_to_DER_cert(Path(args.cert).read_text())
            print('SHA256:', hashlib.sha256(der).hexdigest())
            return
        (receive if args.role=='receive' else transmit)(args)
    except KeyboardInterrupt:
        pass
    except (OSError, ValueError, StopIteration) as exc:
        raise SystemExit('Connection failed: ' + (str(exc) or type(exc).__name__)) from None

if __name__ == '__main__':
    main()
