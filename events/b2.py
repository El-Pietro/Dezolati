import json, time, urllib.request, urllib.parse, math, sys, os
EP=['https://overpass-api.de/api/interpreter']
def ov(q):
    data=urllib.parse.urlencode({'data':q}).encode()
    for i in range(4):
        try:
            req=urllib.request.Request(EP[0],data=data,headers={'User-Agent':'dezolati-dev'})
            return json.load(urllib.request.urlopen(req,timeout=60))
        except Exception as e:
            print('retry',EP[0][:30],e,flush=True); time.sleep(2)
    return None
def rings(e):
    out=[]
    if e['type']=='way' and 'geometry' in e: out.append([(p['lat'],p['lon']) for p in e['geometry']])
    elif e['type']=='relation':
        for m in e.get('members',[]):
            if m.get('role')=='outer' and 'geometry' in m: out.append([(p['lat'],p['lon']) for p in m['geometry']])
    return [r for r in out if len(r)>=4]
def circle(c,r):
    lat,lon=c; pts=[(lat+r/111320*math.cos(2*math.pi*i/16), lon+r/(111320*math.cos(math.radians(lat)))*math.sin(2*math.pi*i/16)) for i in range(16)]
    return [pts+[pts[0]]]
B={'Blansko':'49.33,16.60,49.40,16.70','Boskovice':'49.46,16.62,49.51,16.69','Kunstat':'49.49,16.49,49.52,16.53','Rajec':'49.39,16.62,49.43,16.66',
'Lysice':'49.44,16.52,49.46,16.55','Letovice':'49.53,16.55,49.56,16.60','Ivancice':'49.09,16.36,49.12,16.39','Slapanice':'49.15,16.71,49.18,16.75',
'Praha':'49.94,14.22,50.18,14.71','Brno':'49.15,16.55,49.25,16.68','Jedovnice':'49.33,16.74,49.36,16.78','Adamov':'49.28,16.64,49.31,16.68',
'Oslavany':'49.11,16.32,49.14,16.35','Rosice':'49.17,16.37,49.19,16.40','Sokolnice':'49.10,16.70,49.13,16.74','Lipovec':'49.37,16.78,49.40,16.82'}
T=json.load(open('targets.json'))
done={json.loads(l)['id'] for l in open('out.jsonl')} if os.path.exists('out.jsonl') else set()
t0=time.time()
for t in T:
    if t['id'] in done: continue
    if time.time()-t0>140: print('stop for now',flush=True); break
    bb=B[t['bb']]; rs=[]; src=None
    if t.get('sq'):
        n=t['sq']
        d=ov(f'[out:json][timeout:30];(way["name"="{n}"]["place"="square"]({bb});relation["name"="{n}"]["place"="square"]({bb});way["name"="{n}"]["highway"="pedestrian"]["area"="yes"]({bb});relation["name"="{n}"]["highway"="pedestrian"]({bb}););out tags geom;')
        if d is None: print(t['id'],'FAIL',flush=True); continue
        for e in d['elements']: rs+=rings(e)
        if rs: src='osm-square'
    if not rs and t.get('q'):
        d=ov(f'[out:json][timeout:30];({t["q"].replace("BB",bb)});out center 1;')
        if d is None: print(t['id'],'FAIL',flush=True); continue
        for e in d['elements']:
            c=e.get('center') or {'lat':e.get('lat'),'lon':e.get('lon')}
            if c.get('lat'): rs=circle((c['lat'],c['lon']),t['r']); src=f"point+{t['r']}m"; break
    print(t['id'],src,[len(x) for x in rs],flush=True)
    with open('out.jsonl','a') as f: f.write(json.dumps({'id':t['id'],'name':t['name'],'src':src,'rings':[[[round(a,6),round(b,6)] for a,b in r] for r in rs]},ensure_ascii=False)+'\n')
