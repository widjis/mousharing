"""Batch adjacent mouse deltas without changing click/key ordering."""
import queue
import time


def take_input(events, pending=None, window=0.004):
    event = pending if pending is not None else events.get(timeout=0.1)
    if event.get('type') != 'move':
        return event, None
    event = dict(event)
    deadline = time.monotonic() + window
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return event, None
        try:
            following = events.get(timeout=remaining)
        except queue.Empty:
            return event, None
        if following.get('type') != 'move' or following.get('edge_switch') != event.get('edge_switch'):
            return event, following
        event['dx'] += following['dx']
        event['dy'] += following['dy']
