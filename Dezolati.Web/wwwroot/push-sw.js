// Oznámení od kámošů (web push). Načítá ho service worker (service-worker.js i .published.js).
// Server posílá stejné zprávy jako na Android: data { type, title, body, fromUid, fromNick, emoji, sessionId … }.
// iPhone vyžaduje, aby každá zpráva ukázala oznámení – ukazuje se tedy vždycky.

function dzTarget(type) {
    return ['drink', 'cheer', 'friend', 'invite'].includes(type) ? 'pratele' : '';
}

self.addEventListener('push', event => {
    let p = {};
    try { p = event.data ? event.data.json() : {}; } catch (e) { }
    const d = Object.assign({}, p.notification || {}, p.data || {});
    const type = d.type || 'friend';

    event.waitUntil((async () => {
        // otevřený web se dozví hned (připití naživo, odznak žádostí)
        const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        wins.forEach(c => c.postMessage({ dzPush: d }));

        let title = d.title || 'Dezoláti';
        const opts = {
            body: d.body || '',
            icon: 'icon-192.png',
            badge: 'icon-192.png',
            tag: type + '-' + (d.sessionId || d.fromUid || Date.now()),
            data: { url: dzTarget(type) }
        };

        // připíjení: jedno oznámení na sezení, další připití se do něj připíšou
        if (type === 'cheer' && d.sessionId) {
            opts.tag = 'cheer-' + d.sessionId;
            opts.renotify = true;
            const old = await self.registration.getNotifications({ tag: opts.tag });
            const list = (old[0] && old[0].data && old[0].data.list) || [];
            const nick = d.fromNick || '?';
            const merged = list.filter(x => x.nick !== nick).concat([{ nick, emoji: d.emoji || '🍻' }]);
            opts.data.list = merged;
            if (merged.length > 1) {
                const n = merged.length;
                title = `${n} ${n <= 4 ? 'kámoši si' : 'kámošů si'} s tebou ${n <= 4 ? 'připili' : 'připilo'} 🍻`;
                opts.body = merged.map(x => `${x.nick} ${x.emoji}`).join(', ');
            }
        }
        await self.registration.showNotification(title, opts);
    })());
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = new URL((event.notification.data && event.notification.data.url) || '', self.registration.scope).href;
    event.waitUntil((async () => {
        const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const w of wins) {
            if (w.url.startsWith(self.registration.scope)) {
                await w.focus();
                w.postMessage({ dzOpen: url });
                return;
            }
        }
        await self.clients.openWindow(url);
    })());
});
