"""Build male/female guide dogs from the project dog rig with a guide handle."""
import os, runpy
root=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out=globals().get('OUT_DIR',os.path.dirname(os.path.abspath(__file__)))
for sex in ('Female','Male'):
 runpy.run_path(os.path.join(root,'Dog','build_dog_variant.py'),init_globals={
   'OUT_DIR':out,'NAME':'GuideDog_'+sex,'SEX':sex,'HARNESS':((240,195,62),(170,120,25))})
