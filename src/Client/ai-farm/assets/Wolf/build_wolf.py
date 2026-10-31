"""Build both wolf variants; each wolf is separately named."""
import os, runpy, shutil, sys
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = os.path.abspath(globals().get("OUT_DIR", argv[0] if argv else os.path.dirname(os.path.abspath(__file__))))
for sex in ("Female", "Male"):
    runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)), "build_wolf_variant.py"), init_globals={"OUT_DIR": out, "NAME": "Wolf_" + sex, "SEX": sex})
