import json, time, urllib.request, urllib.parse, math
def ov(q):
    data=urllib.parse.urlencode({'data':q}).encode()
    for i in range(3):
        try:
            req=urllib.request.Request('https://overpass-api.de/api/interpreter',data=data,headers={'User-Agent':'dezolati-dev'})
            return json.load(urllib.request.urlopen(req,timeout=120))
        except Exception as e:
            print('retry',e); time.sleep(5)
    return {'elements':[]}
def rings(e):
    out=[]
    if e['type']=='way' and 'geometry' in e:
        out.append([(p['lat'],p['lon']) for p in e['geometry']])
    elif e['type']=='relation':
        for m in e.get('members',[]):
            if m.get('role')=='outer' and 'geometry' in m: out.append([(p['lat'],p['lon']) for p in m['geometry']])
    return [r for r in out if len(r)>=4]
def square(town, name, extra=''):
    q=f'''[out:json][timeout:90];
area["name"="{town}"]["boundary"="administrative"]->.a;
(way(area.a)["name"="{name}"]["place"="square"]; relation(area.a)["name"="{name}"]["place"="square"];
 way(area.a)["name"="{name}"]["highway"="pedestrian"]["area"="yes"]; relation(area.a)["name"="{name}"]["highway"="pedestrian"];
 {extra});
out tags geom;'''
    d=ov(q); rs=[]
    for e in d['elements']: rs+=rings(e)
    return rs
def point(q):
    d=ov('[out:json][timeout:90];'+q+'out center 1;')
    for e in d['elements']:
        c=e.get('center') or {'lat':e.get('lat'),'lon':e.get('lon')}
        if c.get('lat'): return (c['lat'],c['lon'])
    return None
def circle(c, r):
    lat,lon=c; pts=[]
    for i in range(16):
        a=2*math.pi*i/16
        pts.append((lat+r/111320*math.cos(a), lon+r/(111320*math.cos(math.radians(lat)))*math.sin(a)))
    pts.append(pts[0]); return [pts]

T=[  # id, název, obec (OSM area), náměstí / None, fallback dotaz, poloměr fallbacku
 ('blansko','Blansko – nám. Republiky','Blansko','náměstí Republiky',None,0),
 ('boskovice','Boskovice – Masarykovo nám.','Boskovice','Masarykovo náměstí',None,0),
 ('kunstat','Kunštát – nám. Krále Jiřího','Kunštát','náměstí Krále Jiřího',None,0),
 ('rajec','Rájec-Jestřebí – nám. Míru','Rájec-Jestřebí','náměstí Míru',None,0),
 ('lysice','Lysice – nám. Osvobození','Lysice','náměstí Osvobození',None,0),
 ('letovice','Letovice – Masarykovo nám.','Letovice','Masarykovo náměstí',None,0),
 ('ivancice','Ivančice – Palackého nám.','Ivančice','Palackého náměstí',None,0),
 ('slapanice','Šlapanice – Masarykovo nám.','Šlapanice','Masarykovo náměstí',None,0),
 ('jedovnice','Jedovnice – u kulturního domu',None,None,'area["name"="Jedovnice"]["boundary"="administrative"]->.a;nwr(area.a)["amenity"~"community_centre|theatre|arts_centre"];',70),
 ('lipovec','Lipovec – náves',None,None,'node["place"="village"]["name"="Lipovec"](49.30,16.70,49.45,16.85);',150),
 ('adamov','Adamov – u kostela sv. Barbory',None,None,'area["name"="Adamov"]["boundary"="administrative"]->.a;nwr(area.a)["amenity"="place_of_worship"]["name"~"Barbor"];',70),
 ('oslavany','Oslavany – nádvoří zámku',None,None,'area["name"="Oslavany"]["boundary"="administrative"]->.a;nwr(area.a)["historic"="castle"];',80),
 ('rosice','Rosice – zámek',None,None,'area["name"="Rosice"]["boundary"="administrative"]->.a;nwr(area.a)["historic"="castle"];',80),
 ('sokolnice','Sokolnice – střed obce',None,None,'node["place"="village"]["name"="Sokolnice"](49.05,16.65,49.15,16.80);',180),
 ('praha-staromestske','Praha – Staroměstské nám.','Praha','Staroměstské náměstí',None,0),
 ('praha-vaclavske','Praha – Václavské nám.','Praha','Václavské náměstí',None,0),
 ('praha-miru','Praha – nám. Míru','Praha','náměstí Míru',None,0),
 ('praha-republiky','Praha – nám. Republiky','Praha','náměstí Republiky',None,0),
 ('praha-tylovo','Praha – Tylovo nám.','Praha','Tylovo náměstí',None,0),
 ('praha-jirak','Praha – nám. Jiřího z Poděbrad','Praha','náměstí Jiřího z Poděbrad',None,0),
 ('praha-andel','Praha – Anděl',None,None,'area["name"="Praha"]["boundary"="administrative"]["admin_level"="8"]->.a;node(area.a)["railway"="station"]["name"="Anděl"];',110),
 ('praha-vystaviste','Praha – Výstaviště Holešovice',None,None,'area["name"="Praha"]["boundary"="administrative"]["admin_level"="8"]->.a;nwr(area.a)["name"~"^Výstaviště Praha"];',200),
 ('praha-zbraslav','Praha-Zbraslav – Zbraslavské nám.','Praha','Zbraslavské náměstí',None,0),
 ('brno-svobody','Brno – nám. Svobody','Brno','náměstí Svobody',None,0),
 ('brno-zelny','Brno – Zelný trh','Brno','Zelný trh',None,0),
 ('brno-dominikanske','Brno – Dominikánské nám.','Brno','Dominikánské náměstí',None,0),
 ('brno-moravske','Brno – Moravské nám.','Brno','Moravské náměstí',None,0),
]
out=[]
for tid,label,town,sq,fq,r in T:
    rs=[]
    if sq: rs=square(town,sq)
    src='osm-square' if rs else None
    if not rs and fq:
        c=point(fq)
        if c: rs=circle(c,r); src=f'osm-point+{r}m'
    print(tid, src, len(rs), [len(x) for x in rs])
    out.append({'id':tid,'name':label,'src':src,'rings':[[[round(a,6),round(b,6)] for a,b in ring] for ring in rs]})
    time.sleep(1.5)
json.dump(out,open('trhy_geo.json','w'),ensure_ascii=False)
