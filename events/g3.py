import json,urllib.request,urllib.parse,sys,time
def ov(q):
    data=urllib.parse.urlencode({'data':'[out:json][timeout:40];'+q+'out tags geom;'}).encode()
    for i in range(5):
        try: return json.load(urllib.request.urlopen(urllib.request.Request('https://overpass-api.de/api/interpreter',data=data,headers={'User-Agent':'dezolati-dev'}),timeout=60))
        except Exception as e: print('retry',e,flush=True); time.sleep(4)
def closed(g): return len(g)>=4 and abs(g[0]['lat']-g[-1]['lat'])<1e-7 and abs(g[0]['lon']-g[-1]['lon'])<1e-7
P=lambda g:[[round(p['lat'],6),round(p['lon'],6)] for p in g]
T=json.load(open(sys.argv[1]))
for t in T:
    lat,lon=t['c']; dd=t.get('d',0.004)
    bb=f"{lat-dd},{lon-dd*1.5},{lat+dd},{lon+dd*1.5}"
    d=ov(f'(way["name"="{t["sq"]}"]({bb});relation["name"="{t["sq"]}"]({bb}););')
    if d is None: print(t['id'],'FAIL'); continue
    polys=[];lines=[]
    for e in d['elements']:
        tg=e.get('tags',{})
        if e['type']=='way' and 'geometry' in e:
            g=e['geometry']
            if closed(g) and (tg.get('place')=='square' or tg.get('area')=='yes' or 'area:highway' in tg or (tg.get('highway')=='pedestrian')): polys.append(P(g))
            elif 'highway' in tg: lines.append(P(g))
        elif e['type']=='relation' and tg.get('type')=='multipolygon':
            for m in e.get('members',[]):
                if m.get('role')=='outer' and 'geometry' in m: polys.append(P(m['geometry']))
    zone={'id':t['id'],'name':t['name'],'polys':polys,'lines':lines,'w':t.get('w',22)}
    print(t['id'],'polys',len(polys),'lines',len(lines),flush=True)
    with open('zones.jsonl','a') as f: f.write(json.dumps(zone,ensure_ascii=False)+'\n')
