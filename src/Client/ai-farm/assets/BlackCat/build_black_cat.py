"""Build two black cats using the shared original feline rig."""
import os,runpy
root=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out=globals().get("OUT_DIR",os.path.dirname(os.path.abspath(__file__)))
for sex in ("Female","Male"):
 runpy.run_path(os.path.join(root,"Cat","build_cat_variant.py"),init_globals={"OUT_DIR":out,"NAME":"BlackCat_"+sex,"SEX":sex,"BLACK_CAT":True})
