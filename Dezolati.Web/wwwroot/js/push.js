// Token prohlížeče pro oznámení. Firebase SDK se stahuje až tady (jen když se oznámení zapínají / obnovují).
const SDK = 'https://www.gstatic.com/firebasejs/10.12.5/';

export async function getToken(askPermission) {
    const cfg = window.dzPushConfig || {};
    if (!cfg.appId || !cfg.vapidKey) return 'error:config';
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return 'error:unsupported';

    let permission = Notification.permission;
    if (permission === 'default' && askPermission) permission = await Notification.requestPermission();
    if (permission !== 'granted') return 'error:' + permission;

    const reg = await navigator.serviceWorker.ready;
    const { initializeApp, getApps } = await import(SDK + 'firebase-app.js');
    const { getMessaging, getToken } = await import(SDK + 'firebase-messaging.js');
    const app = getApps().length ? getApps()[0] : initializeApp(cfg);
    const token = await getToken(getMessaging(app), { vapidKey: cfg.vapidKey, serviceWorkerRegistration: reg });
    return token || 'error:empty';
}
