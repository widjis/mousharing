"""Geometry shared by the Mac sender and Windows receiver (no OS input calls)."""
from dataclasses import dataclass
import math

SIDES = ('left', 'right', 'top', 'bottom')
OPPOSITE = {'left':'right', 'right':'left', 'top':'bottom', 'bottom':'top'}


def fraction(value):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError('Non-finite screen position')
    return max(0.0, min(1.0, value))


@dataclass(frozen=True)
class Screen:
    x: float
    y: float
    width: float
    height: float

    def __post_init__(self):
        if (not all(math.isfinite(v) for v in (self.x,self.y,self.width,self.height)) or
                self.width < 2 or self.height < 2):
            raise ValueError('No usable primary screen geometry')

    @property
    def right(self):
        return self.x+self.width-1

    @property
    def bottom(self):
        return self.y+self.height-1

    def clamp(self,x,y):
        return max(self.x,min(self.right,x)), max(self.y,min(self.bottom,y))

    def crossed(self,edge,x,y,dx,dy):
        if edge == 'left':
            return dx < 0 and x <= self.x+1
        if edge == 'right':
            return dx > 0 and x >= self.right-1
        if edge == 'top':
            return dy < 0 and y <= self.y+1
        if edge == 'bottom':
            return dy > 0 and y >= self.bottom-1
        raise ValueError('Invalid screen edge')

    def ratio(self,edge,x,y):
        return fraction((y-self.y)/(self.height-1) if edge in ('left','right')
                        else (x-self.x)/(self.width-1))

    def point(self,edge,ratio,inset=12):
        ratio=fraction(ratio)
        x=self.x+ratio*(self.width-1)
        y=self.y+ratio*(self.height-1)
        if edge == 'left':
            x=self.x+min(inset,(self.width-1)/2)
        elif edge == 'right':
            x=self.right-min(inset,(self.width-1)/2)
        elif edge == 'top':
            y=self.y+min(inset,(self.height-1)/2)
        elif edge == 'bottom':
            y=self.bottom-min(inset,(self.height-1)/2)
        else:
            raise ValueError('Invalid screen edge')
        return self.clamp(x,y)


def windows_primary_screen():
    import ctypes
    user32=ctypes.windll.user32
    # Set awareness before importing pynput so metrics and pointer use the same pixels.
    try:
        user32.SetProcessDpiAwarenessContext.argtypes=[ctypes.c_void_p]
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except AttributeError:
        user32.SetProcessDPIAware()
    return Screen(0,0,user32.GetSystemMetrics(0),user32.GetSystemMetrics(1))
