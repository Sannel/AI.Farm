"""Build the KayKit-style sloth pair in Blender, with embedded rig and clips."""
import os, sys, importlib
from mathutils import Vector
KIT_DIR=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),'AnimalKit')
if KIT_DIR not in sys.path: sys.path.insert(0,KIT_DIR)
import animal_kit as kit
importlib.reload(kit)
OUT_DIR=globals().get('OUT_DIR',os.path.dirname(os.path.abspath(__file__)))
P={'fur':((153,136,112),(99,84,67)),'face':((220,206,174),(168,151,125)),
   'dark':((32,28,30),(15,13,15)),'inner':((160,140,116),(110,92,76)), 'silver':((220,206,174),(168,151,125))}
LEGS={'leg.l':(.10,.03),'leg.r':(-.10,.03)}

def build(sex):
 m=kit.Animal('Sloth_'+sex,OUT_DIR,P)
 # Compact body, broad face, round ears, long hands and curling tail.
 m.part('body','fur',kit.blob((0,0,.52),(.24,.17,.24),subdiv=3))
 m.part('body','face',kit.blob((0,-.16,.50),(.16,.035,.13),subdiv=3))

 m.part('neck','fur',kit.blob((0,-.01,.70),(.105,.10,.11),subdiv=2))
 m.part('head','fur',kit.blob((0,-.03,.85),(.19,.15,.17),subdiv=3))
 m.part('head','face',kit.blob((0,-.153,.83),(.14,.055,.12),subdiv=3))
 m.part('jaw','face',kit.blob((0,-.185,.78),(.09,.04,.045),subdiv=2))
 m.part('head','dark',kit.blob((0,-.207,.81),(.045,.014,.018),subdiv=2))
 for side in (-1,1):
  m.part('head','inner',kit.blob((side*.064,-.196,.865),(.045,.013,.045),subdiv=2))
  m.part('eyes','dark',kit.blob((side*.064,-.204,.865),(.020,.009,.026),subdiv=2))
  bone='arm.l' if side==1 else 'arm.r'
  m.part(bone,'fur',kit.capsule((side*.16,0,.65),(side*.26,-.015,.19),.085,.065,12,3))
  m.part(bone,'face',kit.blob((side*.26,-.025,.19),(.075,.055,.07),subdiv=2))
  for j in (-1,0,1):
   m.part(bone,'face',kit.capsule((side*.26+j*.03,-.05,.16),(side*.26+j*.03,-.11,.12),.012,.006,8,2))
 for leg,(x,y) in LEGS.items():
  m.part(leg,'fur',kit.capsule((x,y,.24),(x,y,.38),.055,.065,12,3))
  m.part(leg+'.lower','fur',kit.capsule((x,y,.055),(x,y,.24),.05,.055,12,3))
  m.part(leg+'.foot','face',kit.blob((x,-.045,.035),(.065,.105,.035),subdiv=2))
 bones=[('root',(0,0,0),None),('body',(0,0,.40),'root'),('neck',(0,0,.70),'body'),('head',(0,-.03,.79),'neck'),('jaw',(0,-.14,.79),'head'),('eyes',(0,-.20,.865),'head'),('arm.l',(.15,0,.63),'body'),('arm.r',(-.15,0,.63),'body'),('tail',(0,.11,.50),'body')]
 bones += [(leg,(x,y,.38),'body') for leg,(x,y) in LEGS.items()]
 bones += [(leg+'.lower',(x,y,.24),leg) for leg,(x,y) in LEGS.items()]
 bones += [(leg+'.foot',(x,y,.035),leg+'.lower') for leg,(x,y) in LEGS.items()]
 m.build(bones,LEGS,.38,.24,.40,foot_tip=.035,leg_radius=.055,tail=('tail',),shade_range=(0,1))
 if sex=='Male':m.rig.scale=(1.16,)*3
 @m.animation('Idle',90)
 def idle(t):
  return m.pose().add('head',rot=(0,8*kit.wave(t),0)).blink(kit.bump(t,.6,.04))
 @m.animation('Idle_LookAround',120)
 def look(t):
  return m.pose().add('head',rot=(0,25*kit.wave(t),0)).blink(kit.bump(t,.7,.04))
 @m.animation('Walking',36)
 def walk(t):
  p=kit.gait(m.pose(),t,kit.BIPED,stance=.65,stride=.16,lift=.06)
  p.add('body',rot=(55,0,0));p.add('head',rot=(-35,0,0))
  p.add('arm.l',rot=(-35+15*kit.wave(t),0,0));p.add('arm.r',rot=(-35-15*kit.wave(t),0,0))
  return p.add('tail',rot=(0,12*kit.wave(t),0))
 @m.animation('Running',24)
 def run(t):
  p=kit.gait(m.pose(),t,kit.BIPED,stance=.45,stride=.24,lift=.09)
  p.add('body',rot=(55,0,0));p.add('head',rot=(-35,0,0))
  p.add('arm.l',rot=(-35+22*kit.wave(t),0,0));p.add('arm.r',rot=(-35-22*kit.wave(t),0,0))
  return p
 @m.animation('Eating_Grass',60)
 def eat(t):
  p=m.pose().add('arm.l',rot=(-70,0,0));p.add('head',rot=(10,0,0));return p.chew(t)
 @m.animation('Being_Fed',60)
 def fed(t):
  return m.pose().add('arm.l',rot=(-70*kit.envelope(t,.05,.2,.8,.95),0,0)).chew(t)
 @m.animation('Getting_Love',90)
 def love(t):return m.pose().add('head',rot=(0,0,12*kit.envelope(t,.05,.2,.8,.95))).blink(.7*kit.envelope(t,.05,.2,.8,.95))
 def lie(p,w):
  for leg in LEGS:p.fold(leg,75,-150,0,w)
  p.add('arm.l',rot=(-90*w,0,0));p.add('arm.r',rot=(-90*w,0,0))
  return p
 @m.animation('Lie_Down',45)
 def down(t):return lie(m.pose(),kit.ramp(t,0,1))
 @m.animation('Lie_Idle',90)
 def lying(t):return lie(m.pose(),1).blink(kit.bump(t,.6,.04))
 @m.animation('Lie_StandUp',45)
 def up(t):return lie(m.pose(),1-kit.ramp(t,0,1))
 @m.animation('Sleeping',120)
 def sleep(t):return lie(m.pose(),1).blink(1)
 @m.animation('Hang_Idle',120)
 def hang(t):
  p=m.pose().add('root',rot=(180,0,3*kit.wave(t)),loc=(0,1.5,0))
  p.add('arm.l',rot=(0,0,8));p.add('arm.r',rot=(0,0,-8))
  return p.blink(kit.bump(t,.6,.05))
 @m.animation('Chitter',60)
 def chatter(t):return m.pose().add('jaw',rot=(15*(1+kit.wave(t,4))/2,0,0)).add('arm.r',rot=(0,0,-15*kit.wave(t)))
 m.finish()
for sex in ('Female','Male'):build(sex)
