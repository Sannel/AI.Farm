"""Build both sheep sexes; keep Sheep.* as compatibility aliases for the ewe.

blender -b --python-exit-code 1 --python build_sheep.py -- <output_dir>
"""
import os
import runpy
import shutil
import sys

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = os.path.abspath(globals().get("OUT_DIR", argv[0] if argv else os.path.dirname(os.path.abspath(__file__))))
preview = os.path.abspath(argv[1]) if len(argv) > 1 else None
for sex in ("Female", "Male"):
    runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)), "build_sheep_variant.py"),
                   init_globals={"OUT_DIR": out, "NAME": "Sheep_" + sex, "SEX": sex,
                                 "PREVIEW_DIR": os.path.join(preview, sex) if preview else None})
for suffix in (".blend", ".glb"):
    shutil.copy2(os.path.join(out, "Sheep_Female" + suffix), os.path.join(out, "Sheep" + suffix))
shutil.copy2(os.path.join(out, "sheep_female_texture.png"), os.path.join(out, "sheep_texture.png"))
