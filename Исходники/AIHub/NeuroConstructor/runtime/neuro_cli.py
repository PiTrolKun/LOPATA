"""Run the local standalone CLI without installing a Python package."""
import sys
from pathlib import Path

# Embedded Windows Python uses an isolated ._pth and omits the script directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lopata_neuro.cli import main

if __name__ == "__main__":
    sys.exit(main())
