"""Build two leopards using the original feline rig."""
import os,runpy
out=globals().get("OUT_DIR",os.path.dirname(os.path.abspath(__file__)))
for sex in ("Female","Male"):
 runpy.run_path(os.path.join(os.path.dirname(os.path.abspath(__file__)),"build_leopard_variant.py"),init_globals={"OUT_DIR":out,"NAME":"Leopard_"+sex,"SEX":sex})
