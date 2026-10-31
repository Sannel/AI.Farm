"""Build a stylized elephant pair with animated trunk, ears and tusks in Blender."""
import os,sys,importlib
KIT_DIR=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),'AnimalKit')
if KIT_DIR not in sys.path:sys.path.insert(0,KIT_DIR)
import animal_kit as kit
importlib.reload(kit)
OUT_DIR=globals().get('OUT_DIR',os.path.dirname(os.path.abspath(__file__)))
P={'skin':((155,164,171),(104,113,121)),'inner':((190,160,161),(142,119,122)),
   'tusk':((249,239,212),(202,187,161)),'eye':((35,30,31),(15,13,13)),
   'nail':((103,101,98),(66,65,63))}
LEGS={'leg.fl':(.45,-.62),'leg.fr':(-.45,-.62),'leg.bl':(.45,.62),'leg.br':(-.45,.62)}
def build(sex):
 e=kit.Animal('Elephant_'+sex,OUT_DIR,P)
 e.part('body','skin',kit.blob((0,0,1.52),(.72,1.02,.72),subdiv=3))
 e.part('neck','skin',kit.blob((0,-.78,1.58),(.58,.45,.58),subdiv=3))
 head=kit.blob((0,-1.03,1.65),(.58,.48,.56),subdiv=3)
 target=kit.surface(head.copy())
 e.part('head','skin',head)
 e.part('jaw','skin',kit.blob((0,-1.23,1.29),(.35,.27,.18),subdiv=2))
 # Single continuous tapered trunk rooted deep into the face.
 points=[(0,-1.37,1.52),(0,-1.58,1.39),(0,-1.66,1.13),(0,-1.67,.84),(0,-1.62,.61),(0,-1.48,.56)]
 widths=[.19,.16,.13,.11,.09,.055]
 e.part('trunk','skin',kit.sweep(points,widths,widths,segments=12))
 for side in (-1,1):
  bone='ear.l' if side==1 else 'ear.r'
  e.part(bone,'skin',kit.blob((side*.54,-.94,1.62),(.34,.075,.45),subdiv=3))
  e.part(bone,'inner',kit.blob((side*.60,-1.005,1.62),(.23,.018,.30),subdiv=2))
  eye=kit.hit(target,(side*.39,-2,1.78),(0,1,0))
  assert eye is not None
  e.part('eyes','eye',kit.blob(eye+kit.Vector((0,.004,0)),(.03,.018,.037),subdiv=2))
  length=.43 if sex=='Male' else .27
  e.part('head','tusk',kit.sweep([(side*.27,-1.30,1.33),(side*.34,-1.52,1.26),(side*.38,-1.30-length,1.37)], [.065,.052,.004],[.065,.052,.004],segments=10))
 for leg,(x,y) in LEGS.items():
  e.part(leg,'skin',kit.capsule((x,y,.53),(x,y,1.12),.22,.26,14,3))
  e.part(leg,'skin',kit.blob((x,y,1.12),(.30,.33,.36),subdiv=3))
  e.part(leg+'.lower','skin',kit.capsule((x,y,.24),(x,y,.53),.20,.22,14,3))
  e.part(leg+'.foot','skin',kit.cylinder((x,y,0),.24,.18,segments=16))
  for dx in (-.12,0,.12):e.part(leg+'.foot','nail',kit.blob((x+dx,y-.20,.07),(.055,.045,.045),subdiv=2))
 e.part('tail','skin',kit.capsule((0,.91,1.55),(0,1.05,.90),.045,.025,10,3))
 e.part('tail','nail',kit.blob((0,1.05,.88),(.055,.05,.09),subdiv=2))
 bones=[('root',(0,0,0),None),('body',(0,0,1.25),'root'),('neck',(0,-.75,1.55),'body'),('head',(0,-1.03,1.65),'neck'),('jaw',(0,-1.20,1.30),'head'),('eyes',(0,-1.39,1.78),'head'),('trunk',(0,-1.37,1.52),'head'),('ear.l',(.48,-.94,1.62),'head'),('ear.r',(-.48,-.94,1.62),'head'),('tail',(0,.91,1.55),'body')]
 bones += [(leg,(x,y,1.12),'body') for leg,(x,y) in LEGS.items()]
 bones += [(leg+'.lower',(x,y,.53),leg) for leg,(x,y) in LEGS.items()]
 bones += [(leg+'.foot',(x,y,0),leg+'.lower') for leg,(x,y) in LEGS.items()]
 e.build(bones,LEGS,1.12,.53,1.25,foot_tip=.24,leg_radius=.25,tail=('tail',),shade_range=(0,2.4))
 if sex=='Male':e.rig.scale=(1.1,)*3
 @e.animation('Idle',90)
 def idle(t):return e.pose().add('trunk',rot=(4*kit.wave(t),0,0)).ears(droop=4*kit.wave(t))
 @e.animation('Idle_LookAround',120)
 def look(t):return e.pose().add('head',rot=(0,12*kit.wave(t),0)).add('trunk',rot=(0,8*kit.wave(t),0))
 @e.animation('Walking',42)
 def walk(t):return kit.gait(e.pose(),t,kit.WALK,stance=.7,stride=.12,lift=.08).add('trunk',rot=(8*kit.wave(t),0,0))
 @e.animation('Running',28)
 def run(t):return kit.gait(e.pose(),t,kit.TROT,stance=.52,stride=.20,lift=.10).add('trunk',rot=(12*kit.wave(t),0,0))
 @e.animation('Eating_Grass',75)
 def eat(t):return e.pose().add('trunk',rot=(20+12*kit.wave(t,2),0,0)).chew(t)
 @e.animation('Being_Fed',60)
 def fed(t):return e.pose().add('trunk',rot=(-35*kit.envelope(t,.1,.3,.7,.9),0,0))
 @e.animation('Getting_Love',90)
 def love(t):return e.pose().ears(droop=12*kit.envelope(t,.1,.3,.7,.9)).blink(.6)
 def lying(p,w):
  for leg in LEGS:p.fold(leg,-70,130,12,w)
  return p.add('trunk',rot=(-45*w,0,0))
 @e.animation('Lie_Down',45)
 def down(t):return lying(e.pose(),kit.ramp(t,0,1))
 @e.animation('Lie_Idle',90)
 def lie(t):return lying(e.pose(),1).blink(kit.bump(t,.6,.04))
 @e.animation('Lie_StandUp',45)
 def up(t):return lying(e.pose(),1-kit.ramp(t,0,1))
 @e.animation('Sleeping',120)
 def sleep(t):return lying(e.pose(),1).blink(1)
 @e.animation('Trumpet',60)
 def trumpet(t):return e.pose().add('trunk',rot=(-65*kit.envelope(t,.1,.3,.7,.9),0,0)).ears(droop=18*kit.envelope(t,.1,.3,.7,.9))
 e.finish()
for sex in ('Female','Male'):build(sex)
