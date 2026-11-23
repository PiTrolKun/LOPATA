import argparse
import json
import os
import pickle
import sys
import tempfile
from pathlib import Path
from zipfile import BadZipFile

from .archive import inspect, pack, unpack


def main(argv=None):
    parser = argparse.ArgumentParser(description="ЛОПАТА · Нейроконструктор")
    actions = parser.add_subparsers(dest="action", required=True)
    build = actions.add_parser("pack")
    build.add_argument("--source", type=Path)
    build.add_argument("--parent", type=Path)
    build.add_argument("--kind", choices=["pair", "trio", "medium","heavy"], required=True)
    build.add_argument("--output", type=Path, required=True)
    build.add_argument("--app-version", required=True)
    build.add_argument("--overrides", type=Path)
    build.add_argument("--settings", type=Path)
    for action in ("inspect", "unpack", "check", "predict"):
        command = actions.add_parser(action)
        command.add_argument("--model", type=Path, required=True)
        if action == "unpack":
            command.add_argument("--output", type=Path, required=True)
        if action in ("predict", "check"):
            command.add_argument("--device", default="auto")
        if action == "check":
            command.add_argument("--suite", choices=["pair", "trio"])
        if action == "predict":
            command.add_argument("--image", required=True)
            command.add_argument("--text", required=True)
            command.add_argument("--audio", nargs=2, default=[])
    args = parser.parse_args(argv)
    # The local weights and tokenizers are the only permitted model sources.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
    try:
        if args.action == "pack":
            settings = json.loads(args.settings.read_text("utf-8-sig")) if args.settings else None
            manifest = pack(args.source, args.kind, args.output, args.app_version, args.parent, args.overrides, settings)
            result = dict(kind=manifest["kind"], file=str(args.output), size=args.output.stat().st_size)
        elif args.action == "inspect":
            result = inspect(args.model)
        elif args.action == "unpack":
            result = unpack(args.model, args.output)
        else:
            with tempfile.TemporaryDirectory(prefix="lopata-neuro-") as directory:
                root = Path(directory) / "model"
                unpack(args.model, root)
                if args.action == "check":
                    from .validation import check
                    result = check(root, args.device, args.suite)
                else:
                    from . import from_directory
                    model = from_directory(root,args.device)
                    try:
                        result = model.predict(args.image,args.text,args.audio)
                    finally:
                        if model.kind in ("medium","heavy"):
                            model.close()
        print(json.dumps(result, ensure_ascii=False, allow_nan=False))
        return int(args.action == "check" and result["total"] != result["matched"])
    except (OSError, ValueError, KeyError, RuntimeError, ImportError, TypeError, EOFError, ArithmeticError,
            BadZipFile, pickle.UnpicklingError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
