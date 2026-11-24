import json, math
def dp(pts, tol_m=2.5):
    if len(pts)<=3: return pts
    lat0=math.radians(pts[0][0]); kx=111320*math.cos(lat0); ky=111320
    def dist(p,a,b):
        px,py=(p[1]-a[1])*kx,(p[0]-a[0])*ky; bx,by=(b[1]-a[1])*kx,(b[0]-a[0])*ky
        L=bx*bx+by*by
        if L==0: return math.hypot(px,py)
        t=max(0,min(1,(px*bx+py*by)/L)); return math.hypot(px-t*bx,py-t*by)
    dmax,idx=0,0
    for i in range(1,len(pts)-1):
        d=dist(pts[i],pts[0],pts[-1])
        if d>dmax: dmax,idx=d,i
    if dmax>tol_m: return dp(pts[:idx+1],tol_m)[:-1]+dp(pts[idx:],tol_m)
    return [pts[0],pts[-1]]
def circle(c,r):
    lat,lon=c; pts=[[round(lat+r/111320*math.cos(2*math.pi*i/16),6), round(lon+r/(111320*math.cos(math.radians(lat)))*math.sin(2*math.pi*i/16),6)] for i in range(16)]
    return pts+[pts[0]]
Z={}
for l in open('out.jsonl'):
    z=json.loads(l)
    if z['rings']: Z[z['id']]={'polys':z['rings'],'lines':[],'w':0}
for l in open('zones.jsonl'):
    z=json.loads(l); Z[z['id']]={'polys':z['polys'],'lines':z['lines'],'w':z['w']}
Z['kunstat']={'polys':[circle((49.50721,16.51846),90)],'lines':[],'w':0}
Z['lysice']={'polys':[circle((49.45175,16.53655),120)],'lines':[],'w':0}
W={'praha-staromestske':45,'praha-jirak':50,'boskovice':35,'praha-vaclavske':30}
M=[
('blansko','Blansko – nám. Republiky (Blanenské Vánoce)','2026-11-25','2026-12-24','potvrzeno','kulturablansko.cz'),
('boskovice','Boskovice – Masarykovo nám. (jarmark + adventní občerstvení)','2026-11-27','2026-12-24','potvrzeno','kulturaboskovice.cz'),
('jedovnice','Jedovnice – u kulturního domu','2026-11-28','2026-11-28','potvrzeno','Blanenský deník'),
('lipovec','Lipovec','2026-11-28','2026-11-28','potvrzeno','Blanenský deník'),
('kunstat','Kunštát – náměstí','2026-11-29','2026-11-29','potvrzeno','Blanenský deník'),
('rajec','Rájec-Jestřebí – nám. Míru','2026-11-29','2026-11-29','potvrzeno','Blanenský deník'),
('lysice','Lysice – nám. Osvobození','2026-11-29','2026-11-29','potvrzeno','Blanenský deník'),
('letovice','Letovice – Masarykovo nám.','2026-11-29','2026-11-29','potvrzeno','Blanenský deník'),
('adamov','Adamov – u kostela sv. Barbory','2026-11-27','2026-11-29','odhad','adamov.cz (2025)'),
('ivancice','Ivančice – Palackého nám.','2026-11-27','2026-11-27','potvrzeno','ivancice.cz'),
('oslavany','Oslavany – nádvoří zámku','2026-11-29','2026-11-29','odhad','Brněnský deník (2025)'),
('rosice','Rosice – zámek','2026-11-29','2026-12-06','odhad','vanocevcesku.cz + Brněnský deník (2025)'),
('slapanice','Šlapanice – Masarykovo nám.','2026-11-29','2026-11-29','potvrzeno','slapanice.cz'),
('sokolnice','Sokolnice – střed obce','2026-11-28','2026-12-20','odhad','Sokolnický zpravodaj 1/2026'),
('praha-staromestske','Praha – Staroměstské nám.','2026-11-28','2027-01-06','potvrzeno','topvanoce.cz'),
('praha-vaclavske','Praha – Václavské nám.','2026-11-28','2027-01-06','potvrzeno','kudyznudy.cz'),
('praha-miru','Praha – nám. Míru','2026-11-20','2026-12-24','potvrzeno','topvanoce.cz'),
('praha-republiky','Praha – nám. Republiky','2026-11-25','2026-12-24','potvrzeno','topvanoce.cz'),
('praha-tylovo','Praha – Tylovo nám.','2026-11-26','2026-12-24','potvrzeno','topvanoce.cz'),
('praha-andel','Praha – Anděl','2026-11-27','2026-12-31','potvrzeno','topvanoce.cz'),
('praha-jirak','Praha – nám. Jiřího z Poděbrad','2026-11-28','2026-12-23','potvrzeno','topvanoce.cz'),
('praha-vystaviste','Praha – Výstaviště Holešovice','2026-12-05','2026-12-06','potvrzeno','topvanoce.cz'),
('praha-zbraslav','Praha-Zbraslav – Zbraslavské nám.','2026-11-28','2026-12-24','odhad','kudyznudy.cz (archiv)'),
('brno-svobody','Brno – nám. Svobody','2026-11-25','2026-12-26','potvrzeno','loudavymkrokem.cz'),
('brno-zelny','Brno – Zelný trh','2026-11-25','2026-12-26','potvrzeno','loudavymkrokem.cz'),
('brno-dominikanske','Brno – Dominikánské nám.','2026-11-25','2026-12-26','potvrzeno','loudavymkrokem.cz'),
('brno-moravske','Brno – Moravské nám.','2026-11-25','2026-12-26','potvrzeno','loudavymkrokem.cz'),
]
out=[]; miss=[]
for mid,name,f,t,st,src in M:
    z=Z.get(mid)
    if not z: miss.append(mid); continue
    out.append({'id':mid,'name':name,'from':f,'to':t,'status':st,'source':src,
        'polys':[dp(p) for p in z['polys']],'lines':[dp(l) for l in z['lines']],'w':W.get(mid,z['w'] or 22)})
json.dump({'v':1,'trhy':out},open('trhy-2026.json','w'),ensure_ascii=False,separators=(',',':'))
import os; print('ok',len(out),'miss',miss,os.path.getsize('trhy-2026.json'))
for o in out: print(o['id'],len(o['polys']),sum(len(p) for p in o['polys']),len(o['lines']),o['w'])
