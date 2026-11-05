from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from amlyrics.__main__ import main

if __name__ == "__main__":
    raise SystemExit(main())
