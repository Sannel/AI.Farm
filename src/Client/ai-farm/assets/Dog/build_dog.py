"""Build both sheepdog variants; Dog remains the original male compatibility alias."""
import os, runpy, shutil, sys
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = os.path.abspath(globals().get("OUT_DIR", argv[0] if argv else os.path.dirname(os.path.abspath(__file__))))
for sex in ("Female", "Male"):
    runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)), "build_dog_variant.py"), init_globals={"OUT_DIR": out, "NAME": "Dog_" + sex, "SEX": sex})
for suffix in (".blend", ".glb"):
    shutil.copy2(os.path.join(out, "Dog_Male" + suffix), os.path.join(out, "Dog" + suffix))
shutil.copy2(os.path.join(out, "dog_male_texture.png"), os.path.join(out, "dog_texture.png"))
