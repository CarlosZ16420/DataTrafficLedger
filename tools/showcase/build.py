"""Frame synthetic-data screenshots with the Hamster Archiver README image tool.

Requires Pillow. Run tests/browser_check.py tests/live-demo --showcase first.
Only adds a rounded frame, transparent margins and a shadow; UI pixels stay intact.
"""
from pathlib import Path
import hamster_frames as frames

ROOT = Path(__file__).resolve().parent
frames.SOURCE_ROOT = ROOT / 'sources'
frames.PREVIEW_ROOT = ROOT / 'output'
frames.WINDOW_SOURCES = {key: key for key in ('dashboard-light', 'dashboard-dark', 'details', 'settings', 'mobile', 'trend-selection', 'time-range')}
frames.CARD_BORDER_COLOR = '#e4eaf2'
frames.SHADOW_COLOR = (23, 42, 71, 36)
frames.WINDOW_MARGIN = 40

if __name__ == '__main__':
    for section in frames.WINDOW_SOURCES:
        print(frames.build_window(section, 'zh', ROOT.parent.parent))
