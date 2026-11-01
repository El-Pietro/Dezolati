// Oznámení od kámošů (web push). Načítá ho service worker (service-worker.js i .published.js).
// Server posílá stejné zprávy jako na Android: data { type, title, body, fromUid, fromNick, emoji, sessionId … }.
// iPhone vyžaduje, aby každá zpráva ukázala oznámení – ukazuje se tedy vždycky.

function dzTarget(type, d) {
    if (type === 'invite' && d && d.inviteId) return 'pozvanky';
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
            tag: type + '-' + (d.inviteId ? d.inviteId + '-' + (d.fromUid || '') : (d.sessionId || d.fromUid || Date.now())),
            data: { url: dzTarget(type, d) }
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

        // kdo pije: jedno společné oznámení „Pijou: Pepa, Žanet a Karel“ (max. 3 h staré záznamy)
        if (type === 'drink') {
            // lidi ze stejné pozvánky mají vlastní oznámení („U Peťky: pijou …“)
            opts.tag = d.inviteId ? 'pijou-' + d.inviteId : 'pijou';
            opts.renotify = true;
            const old = await self.registration.getNotifications({ tag: opts.tag });
            const now = Date.now();
            const list = ((old[0] && old[0].data && old[0].data.list) || []).filter(x => now - x.at < 3 * 3600 * 1000);
            const nick = d.fromNick || (title.split(' si ')[0]) || '?';
            const merged = list.filter(x => x.uid !== d.fromUid).concat([{ uid: d.fromUid, nick, line: title, at: now }]);
            opts.data.list = merged;
            if (merged.length === 1 && d.inviteId) title = `${d.inviteName || 'Pozvánka'}: ${title}`;
            if (merged.length > 1) {
                const nicks = merged.map(x => x.nick);
                const names = nicks.length <= 3
                    ? nicks.slice(0, -1).join(', ') + ' a ' + nicks[nicks.length - 1]
                    : nicks.slice(-2).join(', ') + ` a ${nicks.length - 2} další`;
                title = d.inviteId ? `${d.inviteName || 'Pozvánka'}: pijou ${names}` : `Pijou: ${names}`;
                opts.body = merged.map(x => x.line).reverse().join('\n');
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
