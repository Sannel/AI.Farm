"""Original ground props. Run with Blender --python build_droppings.py -- <output_dir>."""
import os
import sys
import bpy
from mathutils import Vector

out = sys.argv[sys.argv.index('--') + 1] if '--' in sys.argv else os.path.dirname(os.path.abspath(__file__))
os.makedirs(out, exist_ok=True)
# (centre, radii, colour): each prop rests on Z=0 in Blender / Y=0 in Godot.
BROWN = (0.19, 0.095, 0.04, 1)
PALE = (0.72, 0.69, 0.57, 1)
PROPS = {
 'Cow_Pat': [((0,0,.035),(.23,.20,.035),BROWN), ((-.025,0,.065),(.16,.14,.045),BROWN)],
 'Pellets': [((x,y,.022),(.026,.022,.022),BROWN) for x,y in [(-.05,-.03),(.01,-.05),(.06,0),(-.025,.025),(.025,.04),(0,0)]],
 'Horse_Pile': [((x,y,z),(.065,.058,.05),BROWN) for x,y,z in [(-.07,-.03,.05),(.06,-.04,.05),(0,.06,.05),(0,0,.115)]],
 'Pig_Pile': [((-.05,0,.035),(.085,.06,.035),BROWN), ((.04,.02,.04),(.075,.065,.04),BROWN), ((0,0,.085),(.06,.05,.04),BROWN)],
 'Dog_Pile': [((x,y,z),(.065,.032,.03),BROWN) for x,y,z in [(0,-.035,.03),(.02,.025,.03),(-.01,0,.075)]],
 'Chicken_Dropping': [((0,0,.012),(.038,.03,.012),BROWN), ((0,.003,.025),(.018,.015,.009),PALE)],
}
for name, parts in PROPS.items():
 scene = bpy.data.scenes.new(name)
 bpy.context.window.scene = scene
 with bpy.context.temp_override(scene=scene, view_layer=scene.view_layers[0]):
  meshes=[]
  for centre, radii, colour in parts:
   bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=1, location=centre)
   obj=bpy.context.object; obj.scale=radii
   bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
   mat=bpy.data.materials.new(name+'_colour'); mat.diffuse_color=colour; mat.use_nodes=True
   bsdf=mat.node_tree.nodes.get('Principled BSDF');bsdf.inputs['Base Color'].default_value=colour;bsdf.inputs['Roughness'].default_value=.95
   obj.data.materials.append(mat)
   for face in obj.data.polygons: face.use_smooth=True
   meshes.append(obj)
  bpy.ops.object.select_all(action='DESELECT')
  for obj in meshes:obj.select_set(True)
  bpy.context.view_layer.objects.active=meshes[0];bpy.ops.object.join()
  obj=bpy.context.object;obj.name=name
  # Set the origin at ground level, independent of the component centres.
  scene.cursor.location=(0,0,0);bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
  bpy.ops.export_scene.gltf(filepath=os.path.join(out,name+'.glb'),export_format='GLB',use_selection=True,use_active_scene=True,export_animations=False,export_yup=True)
  bpy.data.libraries.write(os.path.join(out,name+'.blend'),{scene},path_remap='RELATIVE_ALL')
  print('EXPORTED',name)
