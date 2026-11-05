"""Own macOS foreground activation for the duration of remote input capture."""


class MacCursorCapture:
    def __init__(self, quartz):
        import AppKit
        self.q=quartz
        self.kit=AppKit
        self.app=AppKit.NSApplication.sharedApplication()
        self.app.setActivationPolicy_(AppKit.NSApplicationActivationPolicyAccessory)
        self.app.finishLaunching()
        self.previous=None
        self.hidden=False
        self.detached=False

    def pump(self):
        # Service activation/deactivation events without opening a window or Dock icon.
        for _ in range(32):
            event=self.app.nextEventMatchingMask_untilDate_inMode_dequeue_(
                self.kit.NSEventMaskAny,self.kit.NSDate.distantPast(),
                self.kit.NSDefaultRunLoopMode,True)
            if event is None:
                break
            self.app.sendEvent_(event)

    def request(self):
        self.previous=self.kit.NSWorkspace.sharedWorkspace().frontmostApplication()
        self.app.activateIgnoringOtherApps_(True)

    def is_active(self):
        return bool(self.app.isActive())

    def acquire(self, display):
        # Activation is asynchronous; the main run loop retries on the next tick.
        if not self.is_active():
            return None
        code=self.q.CGDisplayHideCursor(display)
        if code != 0:
            raise OSError('Cannot hide Mac cursor (code {})'.format(code))
        self.hidden=True
        code=self.q.CGAssociateMouseAndMouseCursorPosition(False)
        if code != 0:
            raise OSError('Cannot capture Mac cursor (code {})'.format(code))
        self.detached=True
        return True

    def release(self, display):
        if self.detached:
            self.q.CGAssociateMouseAndMouseCursorPosition(True)
            self.detached=False
        if self.hidden:
            self.q.CGDisplayShowCursor(display)
            self.hidden=False
        previous,self.previous=self.previous,None
        if previous is not None and self.is_active():
            previous.activateWithOptions_(self.kit.NSApplicationActivateIgnoringOtherApps)
