"""Check exported canine rigs, images, exact clips and ground contact in Blender."""
import os,sys
import bpy,numpy as np
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import verify_animals as common
EXPECTED={'Idle','Walking','Trotting','Running','Stalking','Crouch_Idle','Bark','Lie_Down','Lie_Idle','Lie_StandUp','Sit_Down','Sit_Idle','Sit_StandUp','Getting_Love','Shake','Sleeping','Pooping'}
def verify(folder,name,samples=41):
 sandbox=common.SceneSandbox(); errors=[]
 try:
  for sex in ('Female','Male'):
   sandbox.new_scene();bpy.ops.import_scene.gltf(filepath=os.path.join(folder,name+'_'+sex+'.glb'))
   scene=bpy.context.scene;rigs=[o for o in scene.objects if o.type=='ARMATURE']
   assert len(rigs)==1;rig=rigs[0]
   meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==rig]
   assert len(meshes)==1;obj=meshes[0]
   assert obj.data.uv_layers and all(abs(sum(g.weight for g in v.groups)-1)<.0001 for v in obj.data.vertices)
   images=[n.image for m in obj.data.materials for n in m.node_tree.nodes if n.type=='TEX_IMAGE']
   assert images and all(i is not None and len(i.pixels)>0 for i in images)
   actions={a.name.rsplit('.',1)[0] if a.name.rsplit('.',1)[-1].isdigit() else a.name:a for a in set(bpy.data.actions)-sandbox.actions}
   assert set(actions)==EXPECTED,(sex,set(actions)^EXPECTED)
   for track in rig.animation_data.nla_tracks:track.mute=True
   for clip,action in actions.items():
    rig.animation_data.action=action
    if action.slots:rig.animation_data.action_slot=action.slots[0]
    minimum=100
    for frame in np.linspace(*action.frame_range,samples):
     scene.frame_set(int(frame),subframe=float(frame-int(frame)))
     evaluated=obj.evaluated_get(bpy.context.evaluated_depsgraph_get());mesh=evaluated.to_mesh()
     coords=np.empty(len(mesh.vertices)*3,dtype=np.float32);mesh.vertices.foreach_get('co',coords);coords=coords.reshape(-1,3)
     matrix=np.asarray(evaluated.matrix_world);minimum=min(minimum,float((coords@matrix[2,:3]+matrix[2,3]).min()))
     evaluated.to_mesh_clear()
    if minimum < -.005:errors.append((sex,clip,minimum))
   print(name,sex,len(actions),'clips',flush=True)
 finally:sandbox.restore()
 assert not errors,errors
 print('PASS',name,'both variants',samples,'samples per clip',flush=True)
