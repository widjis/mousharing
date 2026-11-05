"""Text clipboard synchronization; contents are never logged or saved to disk."""
import hashlib
import sys
import threading
import uuid

MAX_TEXT_BYTES=256*1024
POLL_SECONDS=0.25


def checked_text(text):
    if not isinstance(text,str) or '\0' in text:
        raise ValueError('Unsupported clipboard text')
    text=text.replace('\r\n','\n')
    if len(text.encode('utf-8')) > MAX_TEXT_BYTES:
        raise ValueError('Clipboard text exceeds size limit')
    return text


def checked_event(event):
    text=checked_text(event.get('text'))
    clock,origin=event.get('clock'),event.get('origin')
    if (type(clock) is not int or not 1 <= clock < 2**63 or
            not isinstance(origin,str) or len(origin) != 32 or
            any(c not in '0123456789abcdef' for c in origin)):
        raise ValueError('Invalid clipboard revision')
    return text,(clock,origin)


class ClipboardSync:
    def __init__(self,backend):
        self.backend=backend
        self.lock=threading.Lock()
        self.version=None
        self.digest=None
        self.pending=None
        self.clock=0
        self.origin=uuid.uuid4().hex
        self.stamp=(0,'')
        # Baseline only: do not send clipboard contents that predate this session.
        try:
            self.version,text=backend.snapshot()
            if text is not None:
                self.digest=self._digest(checked_text(text))
        except (OSError,ValueError,UnicodeError):
            pass

    @staticmethod
    def _digest(text):
        return hashlib.sha256(text.encode('utf-8')).digest()

    def _apply_pending(self):
        if self.pending is not None:
            text=self.pending
            try:
                version=self.backend.write(text)
            except OSError:
                return False
            self.version=version
            self.digest=self._digest(text)
            self.pending=None
        return True

    def apply(self,text):
        return self.apply_event({'type':'clipboard','text':text,
                                 'clock':self.clock+1,'origin':'0'*32})

    def apply_event(self,event):
        text,stamp=checked_event(event)
        with self.lock:
            self.clock=max(self.clock,stamp[0])
            if stamp <= self.stamp:
                return False
            self.stamp=stamp
            self.pending=text
            return self._apply_pending()

    def poll(self):
        with self.lock:
            if not self._apply_pending():
                return None
            try:
                version,text=self.backend.snapshot()
            except OSError:
                return None
            if self.version is None:
                self.version=version
                try:
                    self.digest=self._digest(checked_text(text)) if text is not None else None
                except (ValueError,UnicodeError):
                    self.digest=None
                return None
            if version == self.version:
                return None
            self.version=version
            if text is None:
                self.digest=None
                return None
            try:
                text=checked_text(text)
            except (ValueError,UnicodeError):
                return None
            digest=self._digest(text)
            if digest == self.digest:
                return None
            self.digest=digest
            self.clock+=1
            self.stamp=(self.clock,self.origin)
            return {'type':'clipboard','text':text,'clock':self.clock,'origin':self.origin}

    def close(self):
        self.pending=None
        self.backend.close()


class MacClipboard:
    def __init__(self,board=None):
        import AppKit
        self.kind=AppKit.NSPasteboardTypeString
        self.board=board if board is not None else AppKit.NSPasteboard.generalPasteboard()
        self.cached_version=None
        self.cached_text=None

    def snapshot(self):
        version=int(self.board.changeCount())
        if version == self.cached_version:
            return version,self.cached_text
        text=self.board.stringForType_(self.kind)
        if int(self.board.changeCount()) != version:
            raise OSError('Clipboard changed during read')
        self.cached_version=version
        self.cached_text=None if text is None or len(text) > MAX_TEXT_BYTES else str(text)
        return version,self.cached_text

    def write(self,text):
        self.board.clearContents()
        if not self.board.setString_forType_(text,self.kind):
            raise OSError('Clipboard write unavailable')
        self.cached_version=int(self.board.changeCount())
        self.cached_text=text
        return self.cached_version

    def close(self):
        pass


class WindowsClipboard:
    CF_UNICODETEXT=13

    def __init__(self):
        import ctypes as c
        from ctypes import wintypes as w
        self.c=c
        self.user=c.WinDLL('user32',use_last_error=True)
        self.kernel=c.WinDLL('kernel32',use_last_error=True)
        declarations=[
            (self.user,'CreateWindowExW',[w.DWORD,w.LPCWSTR,w.LPCWSTR,w.DWORD,c.c_int,c.c_int,c.c_int,c.c_int,w.HWND,w.HMENU,w.HINSTANCE,c.c_void_p],w.HWND),
            (self.user,'DestroyWindow',[w.HWND],w.BOOL),
            (self.user,'OpenClipboard',[w.HWND],w.BOOL),
            (self.user,'CloseClipboard',[],w.BOOL),
            (self.user,'EmptyClipboard',[],w.BOOL),
            (self.user,'GetClipboardSequenceNumber',[],w.DWORD),
            (self.user,'IsClipboardFormatAvailable',[w.UINT],w.BOOL),
            (self.user,'GetClipboardData',[w.UINT],w.HANDLE),
            (self.user,'SetClipboardData',[w.UINT,w.HANDLE],w.HANDLE),
            (self.kernel,'GlobalSize',[w.HGLOBAL],c.c_size_t),
            (self.kernel,'GlobalAlloc',[w.UINT,c.c_size_t],w.HGLOBAL),
            (self.kernel,'GlobalLock',[w.HGLOBAL],c.c_void_p),
            (self.kernel,'GlobalUnlock',[w.HGLOBAL],w.BOOL),
            (self.kernel,'GlobalFree',[w.HGLOBAL],w.HGLOBAL),
            (self.kernel,'GetModuleHandleW',[w.LPCWSTR],w.HMODULE)]
        for library,name,args,result in declarations:
            fn=getattr(library,name)
            fn.argtypes=args
            fn.restype=result
        # A real owner HWND is required by EmptyClipboard/SetClipboardData.
        self.window=self.user.CreateWindowExW(0,'STATIC','mousharing clipboard',0,0,0,0,0,
            c.c_void_p(-3),None,self.kernel.GetModuleHandleW(None),None)
        if not self.window:
            raise c.WinError(c.get_last_error())
        self.cached_version=None
        self.cached_text=None

    def _open(self):
        if not self.user.OpenClipboard(self.window):
            raise OSError('Clipboard busy')

    def snapshot(self):
        version=int(self.user.GetClipboardSequenceNumber())
        if version == self.cached_version:
            return version,self.cached_text
        self._open()
        try:
            version=int(self.user.GetClipboardSequenceNumber())
            if not self.user.IsClipboardFormatAvailable(self.CF_UNICODETEXT):
                return version,None
            handle=self.user.GetClipboardData(self.CF_UNICODETEXT)
            if not handle:
                raise OSError('Clipboard text unavailable')
            size=self.kernel.GlobalSize(handle)
            if size > MAX_TEXT_BYTES*2+2:
                return version,None
            address=self.kernel.GlobalLock(handle)
            if not address:
                raise OSError('Clipboard memory unavailable')
            try:
                text=self.c.string_at(address,size).decode('utf-16-le',errors='replace').split('\0',1)[0]
            finally:
                self.kernel.GlobalUnlock(handle)
            self.cached_version,self.cached_text=version,text
            return version,text
        finally:
            self.user.CloseClipboard()

    def write(self,text):
        data=(text+'\0').encode('utf-16-le')
        handle=self.kernel.GlobalAlloc(0x0002,len(data))
        if not handle:
            raise OSError('Clipboard allocation failed')
        try:
            address=self.kernel.GlobalLock(handle)
            if not address:
                raise OSError('Clipboard memory unavailable')
            try:
                self.c.memmove(address,data,len(data))
            finally:
                self.kernel.GlobalUnlock(handle)
            self._open()
            try:
                if not self.user.EmptyClipboard() or not self.user.SetClipboardData(self.CF_UNICODETEXT,handle):
                    raise OSError('Clipboard write unavailable')
                handle=None  # Windows owns the memory now.
                self.cached_version=int(self.user.GetClipboardSequenceNumber())
                self.cached_text=text
                return self.cached_version
            finally:
                self.user.CloseClipboard()
        finally:
            if handle:
                self.kernel.GlobalFree(handle)

    def close(self):
        if self.window:
            self.user.DestroyWindow(self.window)
            self.window=None


def make_clipboard():
    if sys.platform == 'darwin':
        return MacClipboard()
    if sys.platform == 'win32':
        return WindowsClipboard()
    raise OSError('Clipboard platform unsupported')
