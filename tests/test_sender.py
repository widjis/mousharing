"""Exercise the sender event loop over real TLS with fake Quartz/input controllers."""
import io
import socket
import ssl
import sys
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stdout
from types import SimpleNamespace
from unittest.mock import Mock, patch, call

import mousharing as app
from scripts.init_certificate import create_identity


class FinishedSimulation(Exception):
    pass


class SenderTests(unittest.TestCase):
    def test_mac_edge_to_windows_and_back_restores_cursor(self):
        self.run_simulation(disconnect=False)

    def test_disconnect_while_remote_restores_mac_cursor(self):
        self.run_simulation(disconnect=True)

    def test_hide_failure_never_sends_input_to_windows(self):
        self.run_simulation(disconnect=False,failure='hide')

    def test_detach_failure_never_sends_input_to_windows(self):
        self.run_simulation(disconnect=False,failure='detach')

    def test_disabled_tap_stops_remote_and_restores_cursor(self):
        self.run_simulation(disconnect=False,failure='tap')

    def run_simulation(self,disconnect,failure=None):
        with tempfile.TemporaryDirectory() as directory, redirect_stdout(io.StringIO()):
            cert,key=create_identity(directory)
            context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(cert,key)
            listener=socket.create_server(('127.0.0.1',0))
            host,port=listener.getsockname()
            pointer=Mock(position=(100,100))
            kb=Mock()
            keyboard=SimpleNamespace(Key=SimpleNamespace(),KeyCode=SimpleNamespace())
            mouse=SimpleNamespace(Button=SimpleNamespace(left='left'))
            peer_connections=[]
            class CapturingContext:
                def wrap_socket(self,raw,**kwargs):
                    conn=context.wrap_socket(raw,**kwargs)
                    peer_connections.append(conn)
                    return conn
            def serve():
                with listener:
                    raw,_=listener.accept()
                    app.handle_connection(raw,CapturingContext(),'test-password-123456',kb,pointer,keyboard,mouse,app.Screen(0,0,1920,1080))
            server=threading.Thread(target=serve,daemon=True)
            server.start()
            q=SimpleNamespace()
            for name in ('kCGSessionEventTap','kCGHeadInsertEventTap','kCGEventTapOptionDefault',
                         'kCFRunLoopCommonModes','kCFRunLoopDefaultMode'):
                setattr(q,name,0)
            for bit,name in enumerate(('Command','Shift','Alternate','Control','AlphaShift')):
                setattr(q,'kCGEventFlagMask'+name,1<<bit)
            q.kCGEventTapDisabledByTimeout=1000
            q.kCGEventTapDisabledByUserInput=1001
            for name,field in (('kCGKeyboardEventKeycode','code'),('kCGKeyboardEventAutorepeat','repeat'),
                               ('kCGMouseEventDeltaX','dx'),('kCGMouseEventDeltaY','dy'),
                               ('kCGScrollWheelEventDeltaAxis1','axis1'),('kCGScrollWheelEventDeltaAxis2','axis2')):
                setattr(q,name,field)
            q.CGMainDisplayID=lambda:1
            q.CGDisplayBounds=lambda _:SimpleNamespace(origin=SimpleNamespace(x=0,y=0),size=SimpleNamespace(width=1440,height=900))
            q.CGDisplayHideCursor=Mock(return_value=1 if failure == 'hide' else 0)
            q.CGAssociateMouseAndMouseCursorPosition=Mock(return_value=1 if failure == 'detach' else 0)
            q.kCGEventNull=0
            q.CGEventSetType=Mock(side_effect=lambda event,kind:event.update(nullified=True))
            tap_enabled=True
            q.CGEventTapIsEnabled=lambda _:tap_enabled
            q.CGDisplayShowCursor=Mock(return_value=0)
            q.CGWarpMouseCursorPosition=Mock(return_value=0)
            q.CGEventGetIntegerValueField=lambda e,f:e.get(f,0)
            q.CGEventGetFlags=lambda e:e.get('flags',0)
            q.CGEventGetLocation=lambda e:SimpleNamespace(x=e.get('x',1439),y=e.get('y',450))
            def create_tap(_a,_b,_c,_d,callback,_f):
                q.callback=callback
                return object()
            q.CGEventTapCreate=create_tap
            q.CFMachPortCreateRunLoopSource=lambda *_:object()
            q.CFRunLoopAddSource=lambda *_:None
            q.CFRunLoopGetCurrent=lambda:None
            q.CGEventTapEnable=lambda *_:None
            stage=0
            started=time.monotonic()
            def run_loop(*_):
                nonlocal stage,tap_enabled
                if time.monotonic()-started > 3:
                    raise TimeoutError('Sender did not complete edge roundtrip')
                if stage == 0:
                    # Local clicks pass through and are not sent to Windows.
                    local_click={}
                    self.assertIs(q.callback(None,1,local_click,None),local_click)
                    self.assertIs(q.callback(None,2,local_click,None),local_click)
                    self.assertNotIn('nullified',local_click)
                    crossing={'dx':5,'dy':0}
                    result=q.callback(None,5,crossing,None)
                    if failure in ('hide','detach'):
                        self.assertIs(result,crossing)
                        raise FinishedSimulation()
                    self.assertIsNone(result)
                    self.assertTrue(crossing['nullified'])
                    stage=1
                elif stage == 1 and pointer.position[0] == 12 and failure == 'tap':
                    tap_enabled=False
                    stage=4
                elif stage == 1 and pointer.position[0] == 12 and disconnect:
                    peer_connections[0].shutdown(socket.SHUT_RDWR)
                    stage=4
                elif stage == 1 and pointer.position[0] == 12:
                    for kind in (1,2):
                        remote_click={}
                        self.assertIsNone(q.callback(None,kind,remote_click,None))
                        self.assertTrue(remote_click['nullified'])
                    self.assertIsNone(q.callback(None,5,{'dx':400,'dy':20},None))
                    stage=2
                elif stage == 2 and pointer.position[0] == 412:
                    self.assertIsNone(q.callback(None,5,{'dx':-1000,'dy':0},None))
                    stage=3
                elif stage == 3 and q.CGWarpMouseCursorPosition.called:
                    raise FinishedSimulation()
                time.sleep(0.01)
            q.CFRunLoopRunInMode=run_loop
            args=SimpleNamespace(host=host,port=port,cert=str(cert),windows_side='right',no_edge_switch=False)
            try:
                with patch.dict(sys.modules,{'Quartz':q}), patch.object(app.sys,'platform','darwin'), patch.object(app.getpass,'getpass',return_value='test-password-123456'):
                    if disconnect or failure == 'tap':
                        app.transmit(args)
                    else:
                        with self.assertRaises(FinishedSimulation):
                            app.transmit(args)
            finally:
                server.join(3)
            self.assertFalse(server.is_alive())
            q.CGDisplayHideCursor.assert_called_once_with(1)
            if failure in ('hide','detach'):
                if failure == 'hide':
                    q.CGDisplayShowCursor.assert_not_called()
                    q.CGAssociateMouseAndMouseCursorPosition.assert_not_called()
                else:
                    q.CGDisplayShowCursor.assert_called_once_with(1)
                    q.CGAssociateMouseAndMouseCursorPosition.assert_called_once_with(False)
                q.CGWarpMouseCursorPosition.assert_not_called()
                self.assertEqual(pointer.position,(100,100))
                pointer.press.assert_not_called()
                return
            q.CGAssociateMouseAndMouseCursorPosition.assert_has_calls([call(False),call(True)])
            q.CGDisplayShowCursor.assert_called_once_with(1)
            q.CGWarpMouseCursorPosition.assert_called_once()
            x,y=q.CGWarpMouseCursorPosition.call_args.args[0]
            self.assertEqual(x,1427)
            if disconnect or failure == 'tap':
                self.assertAlmostEqual(y,450)
            else:
                self.assertTrue(450 < y < 480)
            kb.press.assert_not_called()
            if not disconnect and failure is None:
                pointer.press.assert_called_once_with('left')
                pointer.release.assert_called_once_with('left')


if __name__ == '__main__':
    unittest.main()
