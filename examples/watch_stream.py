"""
watch_stream.py

Consumes the USN Watcher JSON output stream from a named pipe.
Run usn-watcher first (Milestone 6), then run this.

Usage:
    python examples/watch_stream.py

Requirements:
    pip install pywin32
"""

import json
import sys
import time

# ── Option A: Read from stdout pipe (Milestones 1-5) ───────────────────────────
# Run as: usn-watcher C --format json | python examples/watch_stream.py

def read_from_stdin():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            handle_event(event)
        except json.JSONDecodeError:
            pass


# ── Option B: Read from named pipe (Milestone 6+) ──────────────────────────────
def read_from_named_pipe():
    import win32pipe
    import win32file

    pipe_name = r'\\.\pipe\usn-watcher'
    print(f"Connecting to {pipe_name}...")

    handle = win32file.CreateFile(
        pipe_name,
        win32file.GENERIC_READ,
        0, None,
        win32file.OPEN_EXISTING,
        0, None
    )

    print("Connected. Listening for events...")
    buffer = b""

    while True:
        try:
            _, data = win32file.ReadFile(handle, 65536)
            buffer += data

            # Split on newlines (NDJSON)
            while b'\n' in buffer:
                line, buffer = buffer.split(b'\n', 1)
                try:
                    event = json.loads(line.decode('utf-8'))
                    handle_event(event)
                except (json.JSONDecodeError, UnicodeDecodeError):
                    pass

        except Exception as e:
            print(f"Pipe disconnected: {e}")
            break


# ── Event Handler ───────────────────────────────────────────────────────────────
def handle_event(event: dict):
    """
    Process a single USN event.
    Customize this function to do whatever you want.
    """
    reasons = event.get('reasons', [])
    filename = event.get('fullPath') or event.get('fileName', '')
    timestamp = event.get('timestamp', '')
    is_dir = event.get('isDirectory', False)

    # Example: only print file saves (CLOSE events on non-directories)
    if 'CLOSE' in reasons and not is_dir:
        ext = filename.rsplit('.', 1)[-1] if '.' in filename else ''
        print(f"[{timestamp[11:23]}] {ext:6} {filename}")


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--pipe':
        read_from_named_pipe()
    else:
        read_from_stdin()
