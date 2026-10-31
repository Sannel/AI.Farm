"""Build a tall, spotted giraffe pair with an animated neck in Blender."""
import os,sys,math,importlib
from mathutils import Vector
KIT_DIR=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),'AnimalKit')
if KIT_DIR not in sys.path:sys.path.insert(0,KIT_DIR)
import animal_kit as kit
importlib.reload(kit)
OUT_DIR=globals().get('OUT_DIR',os.path.dirname(os.path.abspath(__file__)))
P={'gold':((232,187,113),(183,133,72)),'spot':((141,86,45),(93,52,29)),
   'cream':((242,225,191),(195,170,132)),'hoof':((88,72,62),(45,35,30)),
   'eye':((35,27,25),(15,12,11)),'inner':((211,163,143),(162,113,96))}
LEGS={'leg.fl':(.23,-.43),'leg.fr':(-.23,-.43),'leg.bl':(.23,.43),'leg.br':(-.23,.43)}
def build(sex):
 g=kit.Animal('Giraffe_'+sex,OUT_DIR,P)
 def coat(p):
  row=math.floor(p.z/.19)
  a=((p.y+.05*(row%2))%.20)-.10;b=(p.z%.19)-.095
  return 'spot' if (a/.078)**2+(b/.07)**2<1 else 'gold'
 g.part('body',coat,kit.subdivided(kit.body_loft((0,0,1.87),(.65,1.22,.64)),2))
 g.part('neck',coat,kit.subdivided(kit.capsule((0,-.40,2.0),(0,-.53,3.18),.18,.14,16,5),2))
 g.part('head','gold',kit.blob((0,-.56,3.22),(.19,.24,.18),subdiv=3))
 g.part('head','cream',kit.blob((0,-.76,3.13),(.14,.16,.10),subdiv=3))
 g.part('jaw','cream',kit.blob((0,-.74,3.065),(.12,.13,.045),subdiv=2))
 for side in (-1,1):
  g.part('head','hoof',kit.blob((side*.055,-.895,3.14),(.022,.012,.016),subdiv=2))
  g.part('eyes','eye',kit.blob((side*.155,-.69,3.26),(.014,.025,.029),subdiv=2))
  bone='ear.l' if side==1 else 'ear.r'
  g.part(bone,'gold',kit.blob((side*.20,-.51,3.27),(.12,.045,.06),subdiv=2))
  g.part(bone,'inner',kit.blob((side*.21,-.55,3.27),(.075,.012,.035),subdiv=2))
  root=Vector((side*.08,-.49,3.34));tip=root+Vector((0,0,.19))
  g.part('head','gold',kit.tapered(root,tip,.028,.025,segments=10))
  g.part('head','hoof',kit.blob(tip,(.035,.035,.035),subdiv=2))
 for leg,(x,y) in LEGS.items():
  g.part(leg,coat,kit.subdivided(kit.blob((x,y,1.57),(.13,.15,.17),subdiv=3),1))
  g.part(leg,coat,kit.subdivided(kit.capsule((x,y,.78),(x,y,1.52),.075,.105,12,3),1))
  g.part(leg+'.lower',coat,kit.subdivided(kit.capsule((x,y,.1),(x,y,.78),.065,.075,12,3),1))
  g.part(leg+'.lower','hoof',kit.cylinder((x,y,0),.075,.12,segments=12))
 g.part('tail','gold',kit.capsule((0,.56,2.0),(0,.68,1.53),.035,.025,10,3))
 g.part('tail','hoof',kit.blob((0,.68,1.51),(.055,.055,.095),subdiv=2))
 bones=[('root',(0,0,0),None),('body',(0,0,1.65),'root'),('neck',(0,-.40,2.0),'body'),('head',(0,-.53,3.16),'neck'),('jaw',(0,-.65,3.09),'head'),('eyes',(0,-.68,3.26),'head'),('ear.l',(.16,-.51,3.27),'head'),('ear.r',(-.16,-.51,3.27),'head'),('tail',(0,.56,2.0),'body')]
 bones += [(leg,(x,y,1.52),'body') for leg,(x,y) in LEGS.items()]
 bones += [(leg+'.lower',(x,y,.78),leg) for leg,(x,y) in LEGS.items()]
 g.build(bones,LEGS,1.52,.78,1.65,foot_tip=.075,leg_radius=.11,tail=('tail',),shade_range=(0,3.5))
 if sex=='Male':g.rig.scale=(1.08,)*3
 @g.animation('Idle',90)
 def idle(t):return g.pose().add('head',rot=(0,5*kit.wave(t),0)).blink(kit.bump(t,.5,.04))
 @g.animation('Idle_LookAround',120)
 def look(t):return g.pose().add('neck',rot=(0,10*kit.wave(t),0)).add('head',rot=(0,15*kit.wave(t),0))
 @g.animation('Walking',36)
 def walk(t):return kit.gait(g.pose(),t,kit.WALK,stance=.65,stride=.35,lift=.12)
 @g.animation('Running',24)
 def run(t):return kit.gait(g.pose(),t,kit.GALLOP,stance=.45,stride=.55,lift=.18)
 @g.animation('Eating_Grass',90)
 def eat(t):return g.pose().add('neck',rot=(30,0,0)).add('head',rot=(12,0,0)).chew(t)
 @g.animation('Being_Fed',60)
 def fed(t):return g.pose().add('neck',rot=(10*kit.envelope(t,.1,.3,.7,.9),0,0)).chew(t)
 @g.animation('Getting_Love',90)
 def love(t):return g.pose().add('head',rot=(0,0,12*kit.envelope(t,.1,.3,.7,.9))).blink(.5)
 def lying(p,w):
  for leg in LEGS:p.fold(leg,-70,135,12,w)
  return p
 @g.animation('Lie_Down',45)
 def down(t):return lying(g.pose(),kit.ramp(t,0,1))
 @g.animation('Lie_Idle',90)
 def lie(t):return lying(g.pose(),1).blink(kit.bump(t,.6,.04))
 @g.animation('Lie_StandUp',45)
 def up(t):return lying(g.pose(),1-kit.ramp(t,0,1))
 @g.animation('Sleeping',120)
 def sleep(t):return lying(g.pose(),1).blink(1)
 @g.animation('Hum',60)
 def hum(t):return g.pose().add('jaw',rot=(10*kit.envelope(t,.1,.3,.7,.9),0,0))
 g.finish()
for sex in ('Female','Male'):build(sex)
