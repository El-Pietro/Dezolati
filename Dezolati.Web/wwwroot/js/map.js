// Mapa hospod (Leaflet + dlaždice OpenStreetMap) – stejné chování jako mapa v appce:
// hospody až při přiblížení, ve výřezu (+ půl výřezu okolo), při víc než 250 bodech shluky s počtem,
// oblíbené vždy navrch, kámoši modře. Data dodává C# (Pages/Hospody.razor).
window.dzMap = (function () {
    var map = null, ref = null;
    var pubs = [];            // [id, lat, lon, kind, name, flags]  flags: 1 zavřeno, 2 přidané, 4 oblíbené
    var filter = { onlyFav: false, showClosed: true, kinds: { pub: 1, bar: 1, restaurant: 1, biergarten: 1 } };
    var pubLayer = null, clusterLayer = null, friendLayer = null, meLayer = null, homeLayer = null;
    var renderer = null;
    var MIN_ZOOM_PUBS = 13;      // ≈ 19 m/px – jako v appce
    var LABEL_ZOOM = 16;
    var CLUSTER_ABOVE = 250, CLUSTER_PX = 72;

    var COLORS = {
        pub: { fill: '#C27A12', stroke: '#3B2314', r: 6 },
        restaurant: { fill: '#DEC496', stroke: '#6B5A3A', r: 5 },
        closed: { fill: '#AAAAAA', stroke: '#6E6E6E', r: 4.5 },
        fav: { fill: '#E8B020', stroke: '#78460A', r: 8 }
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
        if (p[5] & 1) return COLORS.closed;
        if (p[5] & 4) return COLORS.fav;
        if (p[3] === 'restaurant') return COLORS.restaurant;
        return COLORS.pub;
    }

    function addPub(layer, p) {
        var s = styleOf(p);
        var m;
        if ((p[5] & 2) && !(p[5] & 4) && !(p[5] & 1)) {
            // přidané uživateli: hranatě
            var d = s.r * 1.6;
            m = L.rectangle(map.layerPointToLatLng ? boundsAround(p, d) : [[p[1], p[2]], [p[1], p[2]]],
                { renderer: renderer, color: s.stroke, weight: 2, fillColor: s.fill, fillOpacity: 1 });
        } else {
            m = L.circleMarker([p[1], p[2]], { renderer: renderer, radius: s.r, color: s.stroke, weight: 2, fillColor: s.fill, fillOpacity: 1 });
        }
        m.on('click', function (e) { L.DomEvent.stopPropagation(e); ref.invokeMethodAsync('OnPubClick', p[0]); });
        if (map.getZoom() >= LABEL_ZOOM) {
            m.bindTooltip(escapeHtml(p[4]) + ((p[5] & 1) ? ' (zavřeno)' : ''),
                { permanent: true, direction: 'top', offset: [0, -6], className: 'dz-label' });
        }
        layer.addLayer(m);
    }

    function boundsAround(p, px) {
        var pt = map.latLngToLayerPoint([p[1], p[2]]);
        var a = map.layerPointToLatLng([pt.x - px, pt.y - px]);
        var b = map.layerPointToLatLng([pt.x + px, pt.y + px]);
        return [[a.lat, a.lng], [b.lat, b.lng]];
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
            if (zoom < MIN_ZOOM_PUBS) continue;
            if (p[1] < s || p[1] > n || p[2] < w || p[2] > e) continue;
            visible.push(p);
        }

        if (visible.length <= CLUSTER_ABOVE) {
            visible.forEach(function (p) { addPub(pubLayer, p); });
        } else {
            var buckets = {};
            visible.forEach(function (p) {
                var pt = map.project([p[1], p[2]], zoom);
                var key = Math.floor(pt.x / CLUSTER_PX) + '_' + Math.floor(pt.y / CLUSTER_PX);
                (buckets[key] = buckets[key] || []).push(p);
            });
            Object.keys(buckets).forEach(function (k) {
                var list = buckets[k];
                if (list.length === 1) { addPub(pubLayer, list[0]); return; }
                var lat = 0, lon = 0;
                list.forEach(function (p) { lat += p[1]; lon += p[2]; });
                lat /= list.length; lon /= list.length;
                var size = list.length > 99 ? 40 : 34;
                var icon = L.divIcon({ className: 'dz-cluster', html: '<span>' + list.length + '</span>', iconSize: [size, size] });
                var m = L.marker([lat, lon], { icon: icon, keyboard: false });
                m.on('click', function () { map.setView([lat, lon], Math.min(zoom + 2, 18)); });
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
            map = L.map(elId, { zoomControl: false, attributionControl: true, preferCanvas: true, tap: true });
            renderer = L.canvas({ padding: 0.5, tolerance: 6 });
            L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19,
                attribution: '© přispěvatelé <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>'
            }).addTo(map);
            map.attributionControl.setPrefix(false);
            homeLayer = L.layerGroup().addTo(map);
            pubLayer = L.layerGroup().addTo(map);
            clusterLayer = L.layerGroup().addTo(map);
            friendLayer = L.layerGroup().addTo(map);
            meLayer = L.layerGroup().addTo(map);
            map.setView([lat, lon], zoom);
            map.on('moveend zoomend', scheduleRender);
            map.on('click', function () { ref.invokeMethodAsync('OnMapClick'); });
            setTimeout(function () { map.invalidateSize(); }, 50);
        },
        destroy: function () {
            if (map) { map.remove(); map = null; }
            ref = null;
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
                var m = L.circleMarker([f.lat, f.lon], { radius: 9, color: '#FFFFFF', weight: 3, fillColor: '#466EAA', fillOpacity: 1 });
                m.bindTooltip(escapeHtml(f.label), { permanent: true, direction: 'top', offset: [0, -10], className: 'dz-friend' });
                m.on('click', function (e) { L.DomEvent.stopPropagation(e); ref.invokeMethodAsync('OnFriendClick', f.group); });
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
