import io
import socket
import ssl
import tempfile
import threading
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock

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

    def session(self, token='test-password-123456'):
        listener = socket.create_server(('127.0.0.1',0))
        kb,pointer = Mock(),Mock(position=(100,100))
        keyboard = SimpleNamespace(KeyCode=SimpleNamespace(from_vk=lambda vk:vk),
                                   Key=SimpleNamespace(ctrl='ctrl'))
        mouse = SimpleNamespace(Button=SimpleNamespace(left='left'))
        def serve():
            with listener:
                raw,_ = listener.accept()
                with redirect_stdout(io.StringIO()):
                    app.handle_connection(raw,self.context,'test-password-123456',kb,pointer,keyboard,mouse,app.Screen(0,0,1920,1080))
        thread=threading.Thread(target=serve,daemon=True)
        thread.start()
        client_context=ssl.create_default_context(cafile=str(self.cert))
        client_context.check_hostname=False
        client=client_context.wrap_socket(socket.create_connection(listener.getsockname(),timeout=2))
        client.settimeout(2)
        app.send(client,{'version':app.PROTOCOL_VERSION,'token':token})
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
            app.send(conn,{'type':'move','dx':1,'dy':0,'edge_switch':True})
            self.assertEqual(next(events)['type'],'leave')
        finally:
            conn.shutdown(socket.SHUT_RDWR)
            events.close()
            conn.close()
            thread.join(3)
        self.assertFalse(thread.is_alive())

    def test_wrong_password_cannot_inject_input(self):
        conn,events,thread,kb,pointer=self.session(token='wrong-password')
        try:
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
