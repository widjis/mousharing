import io
import sys
import threading
import unittest
import uuid

import mousharing as app
from clipboard_sync import ClipboardSync, MacClipboard, MAX_TEXT_BYTES, checked_text


class MemoryClipboard:
    def __init__(self,text='before connection'):
        self.text=text
        self.version=1
        self.lock=threading.Lock()
        self.busy_reads=0
        self.busy_writes=0
        self.closed=False

    def copy(self,text):
        with self.lock:
            self.text=text
            self.version+=1
            return self.version

    def snapshot(self):
        with self.lock:
            if self.busy_reads:
                self.busy_reads-=1
                raise OSError('busy')
            return self.version,self.text

    def write(self,text):
        if self.busy_writes:
            self.busy_writes-=1
            raise OSError('busy')
        return self.copy(text)

    def close(self):
        self.closed=True


class ClipboardTests(unittest.TestCase):
    def test_new_copy_syncs_once_and_received_text_does_not_echo(self):
        backend=MemoryClipboard()
        sync=ClipboardSync(backend)
        self.assertIsNone(sync.poll())
        backend.copy('Halo 👋\r\nbaris kedua 中文')
        self.assertEqual(sync.poll()['text'],'Halo 👋\nbaris kedua 中文')
        self.assertIsNone(sync.poll())
        sync.apply('Dari Windows ✅')
        self.assertEqual(backend.text,'Dari Windows ✅')
        self.assertIsNone(sync.poll())
        backend.copy('')
        self.assertEqual(sync.poll()['text'],'')

    def test_busy_clipboard_retries_and_baseline_is_not_exported(self):
        backend=MemoryClipboard('private existing value')
        backend.busy_reads=1
        sync=ClipboardSync(backend)
        self.assertIsNone(sync.poll())
        backend.busy_writes=1
        self.assertFalse(sync.apply('remote value'))
        self.assertIsNone(sync.poll())
        self.assertEqual(backend.text,'remote value')
        self.assertIsNone(sync.poll())

    def test_non_text_and_oversize_are_ignored_and_bad_wire_text_rejected(self):
        backend=MemoryClipboard()
        sync=ClipboardSync(backend)
        for value in (None,'x'*(MAX_TEXT_BYTES+1),'a\0b'):
            backend.copy(value)
            self.assertIsNone(sync.poll())
        for value in (123,'x'*(MAX_TEXT_BYTES+1),'a\0b'):
            with self.assertRaises(ValueError):
                sync.apply(value)
        self.assertEqual(checked_text('é'*100),'é'*100)
        with self.assertRaises(ValueError):
            checked_text('é'*(MAX_TEXT_BYTES//2+1))

    def test_large_clipboard_frame_allowed_but_input_frame_stays_small(self):
        class FakeSocket:
            def __init__(self,data): self.data=data
            def makefile(self,_): return io.BytesIO(self.data)
            def sendall(self,data): self.data=data
        sock=FakeSocket(b'')
        app.send(sock,{'type':'clipboard','text':'Halo 👋\n'*1200,'clock':1,'origin':'a'*32})
        self.assertGreater(len(sock.data),4096)
        self.assertEqual(next(app.messages(sock))['text'],'Halo 👋\n'*1200)
        app.send(sock,{'type':'key','name':'a'*5000})
        with self.assertRaises(ValueError):
            next(app.messages(sock))
        app.send(sock,{'type':'clipboard','text':'x'*(MAX_TEXT_BYTES+1),'clock':1,'origin':'a'*32})
        with self.assertRaises(ValueError):
            next(app.messages(sock))

    def test_simultaneous_copies_converge_and_old_updates_are_ignored(self):
        a,b=MemoryClipboard(),MemoryClipboard()
        left,right=ClipboardSync(a),ClipboardSync(b)
        a.copy('Mac simultaneous copy')
        b.copy('Windows simultaneous copy')
        event_a,event_b=left.poll(),right.poll()
        left.apply_event(event_b)
        right.apply_event(event_a)
        self.assertEqual(a.text,b.text)
        self.assertIsNone(left.poll())
        self.assertIsNone(right.poll())
        a.copy('newer text')
        newer=left.poll()
        right.apply_event(newer)
        right.apply_event(event_b)
        self.assertEqual(b.text,'newer text')

    @unittest.skipUnless(sys.platform == 'darwin','Mac-only native named pasteboard')
    def test_native_mac_roundtrip_uses_isolated_named_pasteboard(self):
        import AppKit
        board=AppKit.NSPasteboard.pasteboardWithName_('mousharing-test-'+uuid.uuid4().hex)
        backend=MacClipboard(board=board)
        try:
            version=backend.write('Uji clipboard 👋\nbaris kedua 中文')
            observed,text=MacClipboard(board=board).snapshot()
            self.assertEqual(version,observed)
            self.assertEqual(text,'Uji clipboard 👋\nbaris kedua 中文')
        finally:
            board.clearContents()
            board.releaseGlobally()
