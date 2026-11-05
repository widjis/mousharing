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

PORT = 47831
PROTOCOL_VERSION = 1
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
    sock.sendall((json.dumps(event, separators=(',', ':'))+'\n').encode())


def messages(sock):
    with sock.makefile('rb') as stream:
        while True:
            line = stream.readline(4097)
            if not line:
                return
            if len(line) > 4096 or not line.endswith(b'\n'):
                raise ValueError('Invalid frame')
            event = json.loads(line)
            if not isinstance(event, dict):
                raise ValueError('Expected object')
            yield event


def receive(args):
    if sys.platform != 'win32':
        raise SystemExit('Receiver runs on Windows.')
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
            handle_connection(raw, context, token, kb, pointer, keyboard, mouse)


def handle_connection(raw, context, token, kb, pointer, keyboard, mouse):
    """One authenticated session; always release pressed inputs on exit."""
    held_keys, held_buttons = set(), set()
    try:
        raw.settimeout(5)
        with context.wrap_socket(raw, server_side=True) as conn:
            events = messages(conn)
            hello = next(events)
            if (hello.get('version') != PROTOCOL_VERSION or
                    not secrets.compare_digest(str(hello.get('token','')).encode('utf-8'), token.encode('utf-8'))):
                raise ValueError('Pairing rejected')
            send(conn, {'ready': True})
            print('Mac connected.', flush=True)
            for event in events:
                kind = event.get('type')
                if kind == 'ping':
                    send(conn, {'type':'pong'})
                    continue
                if kind == 'reset':
                    for key in list(held_keys):
                        kb.release(key)
                        held_keys.discard(key)
                    for button in list(held_buttons):
                        pointer.release(button)
                        held_buttons.discard(button)
                elif kind == 'move':
                    pointer.move(max(-2000,min(2000,int(event['dx']))), max(-2000,min(2000,int(event['dy']))))
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
        raw.close()
        for key in held_keys:
            kb.release(key)
        for button in held_buttons:
            pointer.release(button)


def transmit(args):
    if sys.platform != 'darwin':
        raise SystemExit('Sender runs on macOS.')
    import Quartz as Q
    token = getpass.getpass('Pairing password: ')
    context = ssl.create_default_context(cafile=args.cert)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    # Trust is bound to the supplied self-signed certificate, not the LAN address.
    context.check_hostname = False
    conn = context.wrap_socket(socket.create_connection((args.host,args.port),timeout=5), server_hostname=args.host)
    send(conn, {'token':token, 'version':PROTOCOL_VERSION})
    incoming = messages(conn)
    if next(incoming).get('ready') is not True:
        raise SystemExit('Receiver rejected connection')
    events = queue.Queue(maxsize=2048)
    state = {'remote':False, 'alive':True}
    def enqueue(event):
        try:
            events.put_nowait(event)
        except queue.Full:
            state.update(remote=False,alive=False)
    def disconnect():
        state.update(remote=False,alive=False)
        try:
            conn.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        conn.close()

    def monitor():
        try:
            for reply in incoming:
                if reply.get('type') != 'pong':
                    raise ValueError('Unexpected receiver response')
        except (OSError, ValueError):
            pass
        finally:
            incoming.close()
            disconnect()

    def worker():
        try:
            last_ping = time.monotonic()
            while state['alive']:
                if time.monotonic() - last_ping >= 1:
                    send(conn, {'type':'ping'})
                    last_ping = time.monotonic()
                try:
                    event = events.get(timeout=0.1)
                except queue.Empty:
                    continue
                send(conn,event)
        except OSError:
            pass
        finally:
            disconnect()
            print('Disconnected; Mac input restored.',flush=True)
    threading.Thread(target=worker,daemon=True).start()
    threading.Thread(target=monitor,daemon=True).start()
    modifiers = {54:Q.kCGEventFlagMaskCommand,55:Q.kCGEventFlagMaskCommand,56:Q.kCGEventFlagMaskShift,60:Q.kCGEventFlagMaskShift,58:Q.kCGEventFlagMaskAlternate,61:Q.kCGEventFlagMaskAlternate,59:Q.kCGEventFlagMaskControl,62:Q.kCGEventFlagMaskControl,57:Q.kCGEventFlagMaskAlphaShift}
    def callback(proxy,kind,event,ref):
        if kind in (Q.kCGEventTapDisabledByTimeout,Q.kCGEventTapDisabledByUserInput):
            state['remote']=False
            enqueue({'type':'reset'})
            Q.CGEventTapEnable(tap,True)
            return event
        code = int(Q.CGEventGetIntegerValueField(event,Q.kCGKeyboardEventKeycode)) if kind in (10,11,12) else -1
        # F8 is a dedicated toggle; Escape always returns locally and is reserved.
        if code in (100,53) and kind in (10,11):
            if kind == 10 and not Q.CGEventGetIntegerValueField(event,Q.kCGKeyboardEventAutorepeat):
                state['remote'] = (not state['remote'] and state['alive']) if code == 100 else False
                enqueue({'type':'reset'})
                print('Control: '+('Windows' if state['remote'] else 'Mac'),flush=True)
            return None
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
        elif kind in (5,6,7,27):
            enqueue({'type':'move','dx':Q.CGEventGetIntegerValueField(event,Q.kCGMouseEventDeltaX),'dy':Q.CGEventGetIntegerValueField(event,Q.kCGMouseEventDeltaY)})
        elif kind in (1,2,3,4,25,26):
            name = 'left' if kind in (1,2) else 'right' if kind in (3,4) else 'middle'
            enqueue({'type':'button','name':name,'down':kind in (1,3,25)})
        elif kind == 22:
            enqueue({'type':'scroll','dx':Q.CGEventGetIntegerValueField(event,Q.kCGScrollWheelEventDeltaAxis2),'dy':Q.CGEventGetIntegerValueField(event,Q.kCGScrollWheelEventDeltaAxis1)})
        return None
    mask = sum(1 << n for n in (1,2,3,4,5,6,7,10,11,12,22,25,26,27))
    tap = Q.CGEventTapCreate(Q.kCGSessionEventTap,Q.kCGHeadInsertEventTap,Q.kCGEventTapOptionDefault,mask,callback,None)
    if tap is None:
        disconnect()
        raise SystemExit('Allow Accessibility and Input Monitoring for your terminal, then restart.')
    source = Q.CFMachPortCreateRunLoopSource(None,tap,0)
    Q.CFRunLoopAddSource(Q.CFRunLoopGetCurrent(),source,Q.kCFRunLoopCommonModes)
    Q.CGEventTapEnable(tap,True)
    print('Connected. F8 toggles Mac/Windows. Escape returns to Mac. Ctrl+C exits in local mode.',flush=True)
    try:
        while state['alive']:
            Q.CFRunLoopRunInMode(Q.kCFRunLoopDefaultMode,0.1,False)
    finally:
        Q.CGEventTapEnable(tap,False)
        disconnect()


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
    for command in (receiver,sender):
        command.add_argument('--cert',required=True)
        command.add_argument('--port',type=int,default=PORT)
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
