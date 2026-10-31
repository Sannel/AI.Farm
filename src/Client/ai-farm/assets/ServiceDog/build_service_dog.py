"""Build male/female service dogs from the project dog rig with a service harness."""
import os, runpy
root=os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out=globals().get('OUT_DIR',os.path.dirname(os.path.abspath(__file__)))
for sex in ('Female','Male'):
 runpy.run_path(os.path.join(root,'Dog','build_dog_variant.py'),init_globals={
   'OUT_DIR':out,'NAME':'ServiceDog_'+sex,'SEX':sex,'HARNESS':((220,66,54),(150,35,32))})
