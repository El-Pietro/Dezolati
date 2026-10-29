// Drobnosti, které C# v prohlížeči sám neumí: úložiště, signál, návrat do appky, chvění.
window.dz = (function () {
    var PREFIX = 'dz:';
    var appRef = null;
    var updateWaiting = false;

    function safe(fn, fallback) {
        try { return fn(); } catch (e) { return fallback; }
    }

    return {
        // ---- úložiště (Preferences v appce) ----
        storageAll: function () {
            return safe(function () {
                var out = {};
                for (var i = 0; i < localStorage.length; i++) {
                    var k = localStorage.key(i);
                    if (k && k.indexOf(PREFIX) === 0) out[k] = localStorage.getItem(k);
                }
                return out;
            }, {});
        },
        storageSet: function (k, v) {
            safe(function () {
                if (v === null || v === undefined) localStorage.removeItem(k);
                else localStorage.setItem(k, v);
            });
        },
        // ať Safari data webu přidaného na plochu nemaže
        persist: function () {
            safe(function () { if (navigator.storage && navigator.storage.persist) navigator.storage.persist(); });
        },

        // ---- signál a návrat do appky ----
        isOnline: function () { return navigator.onLine !== false; },
        hook: function (ref) {
            appRef = ref;
            window.addEventListener('online', function () { ref.invokeMethodAsync('OnOnline', true); });
            window.addEventListener('offline', function () { ref.invokeMethodAsync('OnOnline', false); });
            document.addEventListener('visibilitychange', function () {
                if (document.visibilityState === 'visible') ref.invokeMethodAsync('OnResume');
            });
            if (updateWaiting) ref.invokeMethodAsync('OnUpdateReady');
            // zprávy od service workeru: přišlo oznámení / ťuk na oznámení
            if ('serviceWorker' in navigator) {
                navigator.serviceWorker.addEventListener('message', function (e) {
                    var m = e.data || {};
                    if (m.dzPush) ref.invokeMethodAsync('OnPush', JSON.stringify(m.dzPush));
                    if (m.dzOpen) ref.invokeMethodAsync('OnOpen', m.dzOpen);
                });
            }
        },
        updateReady: function () {
            updateWaiting = true;
            if (appRef) appRef.invokeMethodAsync('OnUpdateReady');
        },
        // nová verze čeká → přepnout na ni a načíst znovu
        reload: function () {
            var reg = window.dzReg;
            if (reg && reg.waiting) {
                navigator.serviceWorker.addEventListener('controllerchange', function () { location.reload(); });
                reg.waiting.postMessage('skipWaiting');
            } else {
                location.reload();
            }
        },

        // ---- oznámení (web push) ----
        pushSupported: function () {
            return safe(function () {
                return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
            }, false);
        },
        pushConfigured: function () {
            var c = window.dzPushConfig || {};
            return !!(c.appId && c.vapidKey);
        },
        pushPermission: function () {
            return safe(function () { return 'Notification' in window ? Notification.permission : 'unsupported'; }, 'unsupported');
        },
        // vrací token, nebo „error:…“ (config / unsupported / denied / default / chyba)
        pushToken: async function (askPermission) {
            try {
                var m = await import('./push.js');
                return await m.getToken(askPermission);
            } catch (e) {
                return 'error:' + (e && e.message ? e.message : e);
            }
        },

        // ---- poloha ----
        geo: function (timeoutMs, maxAgeMs) {
            return new Promise(function (resolve) {
                if (!('geolocation' in navigator)) return resolve(JSON.stringify({ ok: false, error: 'unsupported' }));
                var done = false;
                var finish = function (r) { if (!done) { done = true; resolve(JSON.stringify(r)); } };
                setTimeout(function () { finish({ ok: false, error: 'timeout' }); }, timeoutMs + 1500);
                navigator.geolocation.getCurrentPosition(function (p) {
                    finish({ ok: true, lat: p.coords.latitude, lon: p.coords.longitude, acc: p.coords.accuracy });
                }, function (e) {
                    finish({ ok: false, error: e.code === 1 ? 'denied' : e.code === 3 ? 'timeout' : 'unavailable' });
                }, { enableHighAccuracy: false, timeout: timeoutMs, maximumAge: maxAgeMs });
            });
        },
        baseUri: function () { return document.baseURI; },
        openUrl: function (url) { safe(function () { window.open(url, '_blank', 'noopener'); }); },

        // ---- drobnosti ----
        vibrate: function (ms) { safe(function () { if (navigator.vibrate) navigator.vibrate(ms); }); },
        isStandalone: function () {
            return safe(function () {
                return window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
            }, false);
        },
        isIos: function () {
            return /iPad|iPhone|iPod/.test(navigator.userAgent) ||
                (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
        },
        scrollTop: function () { safe(function () { window.scrollTo(0, 0); }); }
    };
})();
