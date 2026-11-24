// Mapa hospod (Leaflet + dlaždice OpenStreetMap) – stejné chování jako mapa v appce:
// hospody ve výřezu (+ půl výřezu okolo), při víc než 250 bodech shluky s počtem,
// oblíbené vždy navrch, kámoši modře. Data dodává C# (Pages/Hospody.razor).
window.dzMap = (function () {
    var map = null, ref = null;
    var pubs = [];            // [id, lat, lon, kind, name, flags]  flags: 1 zavřeno, 2 přidané, 4 oblíbené
    var filter = { onlyFav: false, showClosed: true, kinds: { pub: 1, bar: 1, restaurant: 1, biergarten: 1 } };
    var pubLayer = null, clusterLayer = null, friendLayer = null, meLayer = null, homeLayer = null, trhyLayer = null;
    var renderer = null;
    var trhyLines = [], trhyDots = [];   // event: pásy trhů (tloušťka podle přiblížení) a tečky při oddálení

    function trhyZoom() {
        if (!map || !trhyLayer) return;
        var zoom = map.getZoom();
        trhyLines.forEach(function (t) {
            var mpp = 40075016.686 * Math.cos(t.lat * Math.PI / 180) / Math.pow(2, zoom + 8);
            t.line.setStyle({ weight: Math.max(3, 2 * t.w / mpp) });
        });
        trhyDots.forEach(function (d) {
            if (zoom < 14) { if (!trhyLayer.hasLayer(d)) trhyLayer.addLayer(d); }
            else if (trhyLayer.hasLayer(d)) trhyLayer.removeLayer(d);
        });
    }
    var LABEL_ZOOM = 16;                       // popisky až při přiblížení (v appce pod 3,5 m/px)
    var CLUSTER_ABOVE = 250, CLUSTER_PX = 72;  // víc bodů ve výřezu → shluky s počtem (jako v appce)
    var layerClicked = false;                  // ťuk na značku nemá zároveň zavřít kartičku (klik na mapu)

    // velikosti a barvy jako v appce (Mapsui SymbolStyle: 32 px × měřítko)
    var STYLE = {
        pub: { fill: '#C27A12', stroke: '#3B2314', r: 8, w: 2 },
        restaurant: { fill: '#DEC496', stroke: '#6B5A3A', r: 6.5, w: 2 },
        closed: { fill: '#AAAAAA', stroke: '#6E6E6E', r: 5.5, w: 2 },
        fav: { fill: '#E8B020', stroke: '#78460A', r: 10.5, w: 3 }
    };

    function passes(p) {
        var fav = (p[5] & 4) !== 0;
        if (filter.onlyFav && !fav) return false;
        if (!filter.showClosed && (p[5] & 1)) return false;
        if (fav) return true;
        var k = filter.kinds[p[3]] !== undefined ? p[3] : 'pub';
        return !!filter.kinds[k];
    }

    function styleOf(p) {
        if (p[5] & 1) return STYLE.closed;
        if (p[5] & 4) return STYLE.fav;
        if (p[3] === 'restaurant') return STYLE.restaurant;
        return STYLE.pub;
    }

    function clicked(handler) {
        return function (e) {
            layerClicked = true;
            setTimeout(function () { layerClicked = false; }, 300);
            if (e && e.originalEvent) L.DomEvent.stop(e.originalEvent);
            handler();
        };
    }

    function addPub(layer, p) {
        var s = styleOf(p);
        var m;
        if ((p[5] & 2) && !(p[5] & 4) && !(p[5] & 1)) {
            // přidané uživateli: hospodská barva, ale hranatě
            m = L.marker([p[1], p[2]], {
                icon: L.divIcon({ className: 'dz-custom', iconSize: [14, 14] }),
                keyboard: false
            });
        } else {
            m = L.circleMarker([p[1], p[2]], {
                renderer: renderer, radius: s.r, color: s.stroke, weight: s.w,
                fillColor: s.fill, fillOpacity: 1, bubblingMouseEvents: false
            });
        }
        m.on('click', clicked(function () { ref.invokeMethodAsync('OnPubClick', p[0]); }));
        if (map.getZoom() >= LABEL_ZOOM) {
            m.bindTooltip(escapeHtml(p[4]) + ((p[5] & 1) ? ' (zavřeno)' : ''),
                { permanent: true, direction: 'top', offset: [0, -s.r], className: 'dz-label', interactive: false });
        }
        layer.addLayer(m);
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
    }

    function render() {
        if (!map) return;
        pubLayer.clearLayers();
        clusterLayer.clearLayers();
        var zoom = map.getZoom();
        var b = map.getBounds();
        var mLat = (b.getNorth() - b.getSouth()) / 2, mLon = (b.getEast() - b.getWest()) / 2;
        var s = b.getSouth() - mLat, n = b.getNorth() + mLat, w = b.getWest() - mLon, e = b.getEast() + mLon;

        var favs = [], visible = [];
        for (var i = 0; i < pubs.length; i++) {
            var p = pubs[i];
            if (!passes(p)) continue;
            if (p[5] & 4) { favs.push(p); continue; }
            if (p[1] < s || p[1] > n || p[2] < w || p[2] > e) continue;
            visible.push(p);
        }

        if (visible.length <= CLUSTER_ABOVE) {
            visible.forEach(function (p) { addPub(pubLayer, p); });
        } else {
            // shluky podle vzdálenosti (ne pevná mřížka – ta dělala nepřirozenou šachovnici):
            // vezme bod, přibere všechny volné do CLUSTER_PX okolo, střed = průměr
            var pts = visible.map(function (p) { var pt = map.project([p[1], p[2]], zoom); return { p: p, x: pt.x, y: pt.y, used: false }; });
            var grid = {};
            pts.forEach(function (q) {
                var k = Math.floor(q.x / CLUSTER_PX) + '_' + Math.floor(q.y / CLUSTER_PX);
                (grid[k] = grid[k] || []).push(q);
            });
            // hustá místa první → shluky sedí na centrech měst, ne na náhodném okraji
            pts.sort(function (a, b) {
                var ka = grid[Math.floor(a.x / CLUSTER_PX) + '_' + Math.floor(a.y / CLUSTER_PX)].length;
                var kb = grid[Math.floor(b.x / CLUSTER_PX) + '_' + Math.floor(b.y / CLUSTER_PX)].length;
                return kb - ka;
            });
            var r2 = CLUSTER_PX * CLUSTER_PX;
            pts.forEach(function (q) {
                if (q.used) return;
                q.used = true;
                var members = [q];
                var gx = Math.floor(q.x / CLUSTER_PX), gy = Math.floor(q.y / CLUSTER_PX);
                for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) {
                    var cell = grid[(gx + dx) + '_' + (gy + dy)];
                    if (!cell) continue;
                    cell.forEach(function (o) {
                        if (o.used) return;
                        var ex = o.x - q.x, ey = o.y - q.y;
                        if (ex * ex + ey * ey <= r2) { o.used = true; members.push(o); }
                    });
                }
                if (members.length === 1) { addPub(pubLayer, q.p); return; }
                var mx = 0, my = 0;
                members.forEach(function (o) { mx += o.x; my += o.y; });
                var c = map.unproject([mx / members.length, my / members.length], zoom);
                var count = members.length;
                var size = count < 10 ? 28 : count < 100 ? 32 : count < 1000 ? 38 : 44;
                var icon = L.divIcon({ className: 'dz-cluster', html: '<span>' + count + '</span>', iconSize: [size, size] });
                var m = L.marker(c, { icon: icon, keyboard: false });
                // shluk → přiblížit na něj
                m.on('click', clicked(function () { map.setView(c, Math.min(zoom + 2, 18)); }));
                clusterLayer.addLayer(m);
            });
        }
        favs.forEach(function (p) { addPub(pubLayer, p); });   // oblíbené navrch

        ref.invokeMethodAsync('OnView', b.getSouth(), b.getWest(), b.getNorth(), b.getEast(), zoom, filter.onlyFav);
    }

    var timer = null;
    function scheduleRender() {
        clearTimeout(timer);
        timer = setTimeout(render, 150);
    }

    return {
        create: function (elId, dotnetRef, lat, lon, zoom) {
            ref = dotnetRef;
            if (map) { map.remove(); map = null; }
            map = L.map(elId, { zoomControl: false, attributionControl: true });
            renderer = L.svg({ padding: 0.5 });
            L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19,
                attribution: '© přispěvatelé <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>'
            }).addTo(map);
            map.attributionControl.setPrefix(false);
            trhyLayer = L.layerGroup().addTo(map);   // event: zóny vánočních trhů (pod hospodami)
            homeLayer = L.layerGroup().addTo(map);
            pubLayer = L.layerGroup().addTo(map);
            clusterLayer = L.layerGroup().addTo(map);
            friendLayer = L.layerGroup().addTo(map);
            meLayer = L.layerGroup().addTo(map);
            map.setView([lat, lon], zoom);
            map.on('moveend zoomend', scheduleRender);
            map.on('zoomend', trhyZoom);
            map.on('click', function () { if (!layerClicked) ref.invokeMethodAsync('OnMapClick'); });
            setTimeout(function () { map.invalidateSize(); }, 50);
        },
        destroy: function () {
            if (map) { map.remove(); map = null; }
            ref = null;
        },
        // event: zóny trhů [{id, color, polys, lines, w, cc, r, c}] – polygony, pásy podél ulic (čára o šířce w metrů,
        // tloušťka se přepočítá při přiblížení), kruh; oddálená mapa = jen tečka. Ťuknutí → OnTrhClick
        setTrhy: function (json) {
            if (!trhyLayer) return;
            trhyLayer.clearLayers();
            trhyLines = [];
            trhyDots = [];
            JSON.parse(json || '[]').forEach(function (z) {
                var open = clicked(function () { ref.invokeMethodAsync('OnTrhClick', z.id); });
                var area = { color: z.color, weight: 2, opacity: 0.8, fillColor: z.color, fillOpacity: 0.28 };
                (z.polys || []).forEach(function (r) { L.polygon(r, area).on('click', open).addTo(trhyLayer); });
                (z.lines || []).forEach(function (l) {
                    var pl = L.polyline(l, { color: z.color, opacity: 0.32, lineCap: 'round', lineJoin: 'round', weight: 4 })
                        .on('click', open).addTo(trhyLayer);
                    trhyLines.push({ line: pl, w: z.w, lat: l[0][0] });
                });
                if (z.cc) L.circle(z.cc, Object.assign({ radius: z.r }, area)).on('click', open).addTo(trhyLayer);
                var dot = L.circleMarker(z.c, { radius: 6, color: '#F5F5DC', weight: 2, fillColor: z.color, fillOpacity: 1 }).on('click', open);
                trhyDots.push(dot);
            });
            trhyZoom();
        },
        setPubs: function (json) {
            pubs = JSON.parse(json);
            render();
        },
        setFilter: function (onlyFav, showClosed, kinds) {
            filter.onlyFav = onlyFav;
            filter.showClosed = showClosed;
            filter.kinds = {};
            ['pub', 'bar', 'restaurant', 'biergarten'].forEach(function (k) { filter.kinds[k] = kinds.indexOf(k) >= 0 ? 1 : 0; });
            render();
        },
        setFriends: function (list) {
            if (!map) return;
            friendLayer.clearLayers();
            list.forEach(function (f) {
                var m = L.circleMarker([f.lat, f.lon], { radius: 8, color: '#FFFFFF', weight: 3, fillColor: '#466EAA', fillOpacity: 1, bubblingMouseEvents: false });
                m.bindTooltip(escapeHtml(f.label), { permanent: true, direction: 'top', offset: [0, -10], className: 'dz-friend', interactive: true });
                m.on('click', clicked(function () { ref.invokeMethodAsync('OnFriendClick', f.group); }));
                friendLayer.addLayer(m);
            });
        },
        setMe: function (lat, lon) {
            if (!map) return;
            meLayer.clearLayers();
            L.circleMarker([lat, lon], { radius: 9, color: '#FFFFFF', weight: 3, fillColor: '#286EDC', fillOpacity: 1, interactive: false }).addTo(meLayer);
        },
        setHome: function (lat, lon) {
            if (!map) return;
            homeLayer.clearLayers();
            if (lat === null || lat === undefined) return;
            var icon = L.divIcon({ className: 'dz-home', html: '▲', iconSize: [22, 22] });
            L.marker([lat, lon], { icon: icon, interactive: false, keyboard: false }).addTo(homeLayer);
        },
        flyTo: function (lat, lon, zoom) {
            if (map) map.setView([lat, lon], zoom || map.getZoom());
        },
        center: function () {
            if (!map) return null;
            var c = map.getCenter();
            return [c.lat, c.lng];
        },
        refresh: function () { if (map) { map.invalidateSize(); render(); } }
    };
})();
