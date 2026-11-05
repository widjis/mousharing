import io
import socket
import ssl
import tempfile
import threading
import unittest
from contextlib import redirect_stdout, suppress
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock
from test_clipboard import MemoryClipboard
from clipboard_sync import ClipboardSync

import mousharing as app
from scripts.init_certificate import create_identity


class ProtocolTests(unittest.TestCase):
    def test_framing_rejects_truncation_and_oversize(self):
        for data in (b'{"type":"ping"}', b'x'*4097, b'[]\n'):
            a,b = socket.socketpair()
            with a,b:
                a.sendall(data)
                a.shutdown(socket.SHUT_WR)
                with self.assertRaises(ValueError):
                    list(app.messages(b))

    def test_multiple_events_round_trip(self):
        a,b = socket.socketpair()
        events = [{'type':'key','name':'a','down':True}, {'type':'reset'}]
        with a,b:
            for event in events:
                app.send(a,event)
            a.shutdown(socket.SHUT_WR)
            self.assertEqual(list(app.messages(b)),events)

    def test_physical_shortcut_keys(self):
        expected = {0:'a',9:'v',11:'b',16:'y',17:'t',31:'o',35:'p'}
        for code,name in expected.items():
            self.assertEqual(app.CODES[code],name)
            self.assertEqual(app.WINDOWS_VK[name],ord(name.upper()))


class TLSSessionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory()
        with redirect_stdout(io.StringIO()):
            cls.cert,cls.key = create_identity(cls.directory.name)
        cls.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        cls.context.load_cert_chain(cls.cert,cls.key)

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def session(self, token='test-password-123456',clipboard_factory=None,clipboard_enabled=True,protocol_version=None):
        listener = socket.create_server(('127.0.0.1',0))
        kb,pointer = Mock(),Mock(position=(100,100))
        keyboard = SimpleNamespace(KeyCode=SimpleNamespace(from_vk=lambda vk:vk),
                                   Key=SimpleNamespace(ctrl='ctrl'))
        mouse = SimpleNamespace(Button=SimpleNamespace(left='left'))
        def serve():
            with listener:
                raw,_ = listener.accept()
                with redirect_stdout(io.StringIO()):
                    app.handle_connection(raw,self.context,'test-password-123456',kb,pointer,keyboard,mouse,app.Screen(0,0,1920,1080),clipboard_factory=clipboard_factory,clipboard_enabled=clipboard_enabled)
        thread=threading.Thread(target=serve,daemon=True)
        thread.start()
        client_context=ssl.create_default_context(cafile=str(self.cert))
        client_context.check_hostname=False
        client=client_context.wrap_socket(socket.create_connection(listener.getsockname(),timeout=2))
        client.settimeout(2)
        app.send(client,{'version':app.PROTOCOL_VERSION if protocol_version is None else protocol_version,'token':token,'clipboard':clipboard_factory is not None})
        return client,app.messages(client),thread,kb,pointer

    def test_auth_heartbeat_reset_and_disconnect_release(self):
        conn,events,thread,kb,pointer=self.session()
        try:
            self.assertTrue(next(events)['ready'])
            app.send(conn,{'type':'enter','side':'right','ratio':0.5,'switch_id':1})
            app.send(conn,{'type':'key','name':'c','down':True})
            app.send(conn,{'type':'button','name':'left','down':True})
            app.send(conn,{'type':'reset'})
            app.send(conn,{'type':'enter','side':'right','ratio':0.5,'switch_id':2})
            app.send(conn,{'type':'key','name':'ctrl','down':True})
            app.send(conn,{'type':'ping'})
            self.assertEqual(next(events),{'type':'pong'})
        finally:
            conn.shutdown(socket.SHUT_RDWR)
            events.close()
            conn.close()
            thread.join(3)
        self.assertFalse(thread.is_alive())
        kb.press.assert_any_call(ord('C'))
        kb.release.assert_any_call(ord('C'))
        kb.release.assert_any_call('ctrl')
        pointer.release.assert_called_with('left')

    def test_shared_edge_returns_control_and_ignores_queued_input(self):
        conn,events,thread,kb,pointer=self.session()
        try:
            next(events)
            app.send(conn,{'type':'enter','side':'right','ratio':0.25,'switch_id':7})
            app.send(conn,{'type':'move','dx':-100,'dy':0,'edge_switch':True})
            reply=next(events)
            self.assertEqual(reply['type'],'leave')
            self.assertEqual(reply['switch_id'],7)
            self.assertAlmostEqual(reply['ratio'],0.25,places=2)
            app.send(conn,{'type':'key','name':'c','down':True})
            app.send(conn,{'type':'move','dx':100,'dy':0,'edge_switch':True})
            app.send(conn,{'type':'ping'})
            self.assertEqual(next(events),{'type':'pong'})
            self.assertEqual(pointer.position[0],0)
            kb.press.assert_not_called()
        finally:
            conn.shutdown(socket.SHUT_RDWR)
            events.close()
            conn.close()
            thread.join(3)
        self.assertFalse(thread.is_alive())

    def test_drag_does_not_switch_screen_until_button_released(self):
        conn,events,thread,kb,pointer=self.session()
        try:
            next(events)
            app.send(conn,{'type':'enter','side':'left','ratio':0.5,'switch_id':8})
            app.send(conn,{'type':'button','name':'left','down':True})
            app.send(conn,{'type':'move','dx':100,'dy':0,'edge_switch':True})
            app.send(conn,{'type':'ping'})
            self.assertEqual(next(events),{'type':'pong'})
            app.send(conn,{'type':'button','name':'left','down':False})
            app.send(conn,{'type':'move','dx':40,'dy':0,'edge_switch':True})
            self.assertEqual(next(events)['type'],'leave')
        finally:
            conn.shutdown(socket.SHUT_RDWR)
            events.close()
            conn.close()
            thread.join(3)
        self.assertFalse(thread.is_alive())

    def test_clipboard_bidirectional_over_tls_without_input_focus_or_echo(self):
        windows=MemoryClipboard('existing Windows clipboard')
        mac=MemoryClipboard('existing Mac clipboard')
        mac_sync=ClipboardSync(mac)
        conn,events,thread,kb,pointer=self.session(clipboard_factory=lambda:windows)
        try:
            self.assertTrue(next(events)['clipboard'])
            mac.copy('Mac → Windows 👋\n'*1200)
            app.send(conn,mac_sync.poll())
            app.send(conn,{'type':'ping'})
            self.assertEqual(next(events),{'type':'pong'})
            self.assertEqual(windows.text,mac.text)
            windows.copy('Windows → Mac ✅\n'*1200)
            received=next(events)
            self.assertEqual(received['type'],'clipboard')
            mac_sync.apply_event(received)
            self.assertEqual(mac.text,windows.text)
            self.assertIsNone(mac_sync.poll())
            app.send(conn,{'type':'ping'})
            self.assertEqual(next(events),{'type':'pong'})
            kb.press.assert_not_called()
        finally:
            conn.shutdown(socket.SHUT_RDWR)
            events.close()
            conn.close()
            thread.join(3)
        self.assertFalse(thread.is_alive())
        self.assertTrue(windows.closed)

    def test_clipboard_is_never_opened_before_authentication_or_when_disabled(self):
        for password,enabled in [('wrong-password',True),('test-password-123456',False)]:
            factory=Mock(return_value=MemoryClipboard())
            conn,events,thread,kb,pointer=self.session(token=password,clipboard_factory=factory,clipboard_enabled=enabled)
            try:
                if password == 'wrong-password':
                    rejected=next(events)
                    self.assertFalse(rejected['ready'])
                    self.assertEqual(rejected['error'],'pairing')
                    self.assertEqual(list(events),[])
                else:
                    self.assertFalse(next(events)['clipboard'])
                    app.send(conn,{'type':'clipboard','text':'must not be applied','clock':1,'origin':'a'*32})
                    app.send(conn,{'type':'ping'})
                    self.assertEqual(next(events),{'type':'pong'})
            finally:
                with suppress(OSError):
                    conn.shutdown(socket.SHUT_RDWR)
                events.close()
                conn.close()
                thread.join(3)
            factory.assert_not_called()
            self.assertFalse(thread.is_alive())

    def test_wrong_protocol_reports_update_required_before_clipboard_access(self):
        factory=Mock(return_value=MemoryClipboard())
        conn,events,thread,kb,pointer=self.session(clipboard_factory=factory,protocol_version=2)
        try:
            rejected=next(events)
            self.assertFalse(rejected['ready'])
            self.assertEqual(rejected['error'],'protocol')
            self.assertEqual(list(events),[])
        finally:
            events.close()
            conn.close()
            thread.join(3)
        factory.assert_not_called()
        kb.press.assert_not_called()
        self.assertFalse(thread.is_alive())

    def test_wrong_password_cannot_inject_input(self):
        conn,events,thread,kb,pointer=self.session(token='wrong-password')
        try:
            rejected=next(events)
            self.assertFalse(rejected['ready'])
            self.assertEqual(rejected['error'],'pairing')
            self.assertEqual(list(events),[])
        finally:
            events.close()
            conn.close()
            thread.join(3)
        kb.press.assert_not_called()
        pointer.press.assert_not_called()
        self.assertFalse(thread.is_alive())

    def test_untrusted_certificate_rejected(self):
        listener=socket.create_server(('127.0.0.1',0))
        failures=[]
        def serve():
            with listener:
                raw,_=listener.accept()
                try:
                    with self.context.wrap_socket(raw,server_side=True):
                        pass
                except ssl.SSLError:
                    failures.append(True)
                finally:
                    raw.close()
        thread=threading.Thread(target=serve,daemon=True)
        thread.start()
        with socket.create_connection(listener.getsockname(),timeout=2) as raw:
            with self.assertRaises(ssl.SSLCertVerificationError):
                ssl.create_default_context().wrap_socket(raw,server_hostname='untrusted')
        thread.join(3)
        self.assertFalse(thread.is_alive())
        self.assertTrue(failures)


if __name__ == '__main__':
    unittest.main()
