"""Build the canonical payload with return-type-independent Harmony Patch calls.

python build_compatible.py --builder PATH/build_payload.py --out DIR --tag vTAG
Canonical sources stay unchanged; generated sources route install-time Patch calls.
"""
import argparse
import importlib.util
from pathlib import Path
import re
import sys

CALL = re.compile(r'\b(_harmony|harmony|h|_aceProbeHarmony|_panelHarmony)\s*\.\s*Patch\s*\(')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--builder', type=Path, required=True)
    parser.add_argument('--vam-root', type=Path)
    parser.add_argument('--source', type=Path)
    args, rest = parser.parse_known_args()
    spec = importlib.util.spec_from_file_location('canonical_builder', args.builder)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if args.vam_root:
        old_root = module.ROOT
        module.ROOT = str(args.vam_root.resolve())
        module.REFS = [(d.replace(old_root, module.ROOT), f) for d, f in module.REFS]
    if args.source:
        module.SRC = str(args.source.resolve())
        module.MEMORY_LIST = str(Path(module.SRC).parent / 'memory_modules.txt')
    original = module.transform

    def transform(text, tag):
        text = CALL.sub(lambda m: 'HarmonyCompat.Patch(' + m.group(1) + ', ', text)
        return original(text, tag)

    module.transform = transform
    # The canonical builder copies all non-memory sources from its SRC directory.
    adapter = Path(module.SRC) / 'HarmonyCompat.cs'
    if not adapter.exists():
        raise SystemExit('Place HarmonyCompat.cs in canonical plugin_sources before building')
    sys.argv = [str(args.builder), *rest]
    module.main()


if __name__ == '__main__':
    main()
