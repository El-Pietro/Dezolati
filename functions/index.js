/**
 * Dezoláti – Cloud Functions (Firebase Blaze, reálně 0 Kč: 2 mil. volání měsíčně zdarma).
 *
 * Posílá oznámení na telefony kámošů (FCM). Appka sama posílat nesmí – musela by mít servisní klíč.
 *  - friendRequest:  nová žádost o přátelství  → adresátovi
 *  - friendAccepted: žádost přijata            → žadateli
 *  - atThePub:       první drink sezení (bez 3h pauzy) → kámošům „Žanet si právě dala pivo, připij si s ní!“
 *  - cheer:          reakce 🍺 🍷 🥃 → „Petr si s tebou připíjí 🍺“
 *  - invite:         pozvánka na drink → kámošům (telefon sám pozná, jestli je do 15 km)
 *
 * Tokeny telefonů: PushTokens/{hash} { Uid, Token }. Kdo nechce: Users/{uid}.NotifyFriends == false.
 *  - usage / usageWatch: přehled spotřeby proti bezplatným limitům (jen pro admina) + hlídání každou hodinu
 *
 * Nasazení: firebase deploy --only functions   (viz functions/README.md)
 */
const { onDocumentCreated, onDocumentUpdated } = require("firebase-functions/v2/firestore");
const { onCall, HttpsError } = require("firebase-functions/v2/https");
const { onSchedule } = require("firebase-functions/v2/scheduler");
const { defineString } = require("firebase-functions/params");
const monitoring = require("@google-cloud/monitoring");
const { setGlobalOptions, logger } = require("firebase-functions/v2");
const { initializeApp } = require("firebase-admin/app");
const { getFirestore, FieldValue, Timestamp } = require("firebase-admin/firestore");
const { getMessaging } = require("firebase-admin/messaging");

// MUSÍ sedět s umístěním databáze Firestore (Google Cloud → Firestore → Databases → Location).
// Databáze Dezolátů je v europe-west10 (Berlín) → funkce běží tamtéž.
const REGION = "europe-west10";

setGlobalOptions({ region: REGION, maxInstances: 3, memory: "256MiB" });

initializeApp();
const db = getFirestore();

// ---------------- pomocné ----------------

/** Uživatelé, kteří oznámení chtějí (NotifyFriends není false). */
async function wantingNotifications(uids) {
  const unique = [...new Set(uids.filter(Boolean))];
  if (unique.length === 0) return [];
  const refs = unique.map((uid) => db.doc(`Users/${uid}`));
  const docs = await db.getAll(...refs);
  return docs.filter((d) => !d.exists || d.get("NotifyFriends") !== false).map((d) => d.id);
}

/** Tokeny telefonů daných uživatelů (dotaz „in“ bere max. 30 hodnot). */
async function tokensOf(uids) {
  const result = []; // [{ token, ref }]
  for (let i = 0; i < uids.length; i += 30) {
    const snap = await db.collection("PushTokens").where("Uid", "in", uids.slice(i, i + 30)).get();
    snap.forEach((d) => result.push({ token: d.get("Token"), ref: d.ref }));
  }
  return result.filter((t) => t.token);
}

/** Pošle oznámení uživatelům; neplatné tokeny (odinstalovaná appka) smaže. */
async function notify(uids, title, body, data = {}) {
  const wanting = await wantingNotifications(uids);
  if (wanting.length === 0) return;
  const tokens = await tokensOf(wanting);
  if (tokens.length === 0) return;

  const res = await getMessaging().sendEachForMulticast({
    tokens: tokens.map((t) => t.token),
    notification: { title, body },
    data: { title, body, ...data },
    android: {
      priority: "high",
      notification: { channelId: "kamosi", icon: "ic_stat_beer", color: "#6B3E1E" },
    },
  });

  const dead = [];
  res.responses.forEach((r, i) => {
    const code = r.error?.code;
    if (code === "messaging/registration-token-not-registered" || code === "messaging/invalid-registration-token") {
      dead.push(tokens[i].ref.delete());
    }
  });
  await Promise.all(dead);
  logger.info(`Oznámení „${title}“: ${res.successCount} ok, ${res.failureCount} chyb`);
}

async function nickOf(uid, fallback = "Někdo") {
  const d = await db.doc(`Users/${uid}`).get();
  return (d.exists && d.get("Nickname")) || fallback;
}

// ---------------- přátelé ----------------

exports.friendRequest = onDocumentCreated("Friends/{id}", async (event) => {
  const f = event.data?.data();
  if (!f || f.Status !== "Pending" || !f.FriendId) return;
  const from = f.Nicks?.[f.UserId] || (await nickOf(f.UserId));
  await notify([f.FriendId], "Žádost o přátelství", `${from} si tě chce přidat mezi kámoše.`, { type: "friend" });
});

exports.friendAccepted = onDocumentUpdated("Friends/{id}", async (event) => {
  const before = event.data?.before?.data();
  const after = event.data?.after?.data();
  if (!before || !after) return;
  if (before.Status === "Accepted" || after.Status !== "Accepted") return;
  const who = after.Nicks?.[after.FriendId] || (await nickOf(after.FriendId));
  await notify([after.UserId], "Žádost přijata", `${who} tě přijal(a) mezi kámoše. 🍺`, { type: "friend" });
});

// ---------------- datové zprávy (oznámení si kreslí appka – kvůli tlačítkům reakcí) ----------------

/** Pošle datovou zprávu (bez systémového oznámení) těm, kdo oznámení chtějí. Hodnoty musí být řetězce. */
async function notifyData(uids, data) {
  const wanting = await wantingNotifications(uids);
  if (wanting.length === 0) return;
  const tokens = await tokensOf(wanting);
  if (tokens.length === 0) return;
  const payload = {};
  for (const [k, v] of Object.entries(data)) if (v !== undefined && v !== null) payload[k] = String(v);

  const res = await getMessaging().sendEachForMulticast({
    tokens: tokens.map((t) => t.token),
    data: payload,
    android: { priority: "high" },
  });
  const dead = [];
  res.responses.forEach((r, i) => {
    const code = r.error?.code;
    if (code === "messaging/registration-token-not-registered" || code === "messaging/invalid-registration-token") {
      dead.push(tokens[i].ref.delete());
    }
  });
  await Promise.all(dead);
  logger.info(`Zpráva ${payload.type} „${payload.title}“: ${res.successCount} ok, ${res.failureCount} chyb`);
}

/** Přijatí kámoši uživatele (jen array-contains → bez složeného indexu). */
async function friendsOf(uid) {
  const snap = await db.collection("Friends").where("Members", "array-contains", uid).get();
  return snap.docs
    .filter((f) => f.get("Status") === "Accepted")
    .map((f) => (f.get("UserId") === uid ? f.get("FriendId") : f.get("UserId")))
    .filter(Boolean);
}

/** „pivo“, „2 piva“, „víno a panáka“ … (4. pád). */
function whatDrank(d) {
  const n = (x) => Math.max(0, Number(x) || 0);
  const form = (k, one, few, many) => (k === 1 ? one : k >= 2 && k <= 4 ? `${k} ${few}` : `${k} ${many}`);
  const parts = [];
  if (n(d.Pivo) > 0) parts.push(form(n(d.Pivo), "pivo", "piva", "piv"));
  if (n(d.Vino) > 0) parts.push(form(n(d.Vino), "víno", "vína", "vín"));
  if (n(d.Panaky) > 0) parts.push(form(n(d.Panaky), "panáka", "panáky", "panáků"));
  if (parts.length === 0) return "něco";
  return parts.length === 1 ? parts[0] : parts.slice(0, -1).join(", ") + " a " + parts[parts.length - 1];
}

// ---------------- sezení: „Žanet si právě dala pivo. Připij si s ní!“ ----------------
//
// Sezení = pití bez 3h pauzy. První drink sezení → kámošům oznámení (s tlačítky reakcí 🍺 🍷 🥃),
// do profilu id sezení (na něj se reaguje). Ráno a večer = 2 sezení, nonstop = 1.
// (Jméno funkce zůstává atThePub, ať se při nasazení nemusí mazat stará.)

const SESSION_GAP_MS = 3 * 60 * 60 * 1000;

exports.atThePub = onDocumentCreated("Drinks/{id}", async (event) => {
  const d = event.data?.data();
  if (!d || d.Correction || !d.UserId) return;
  const total = (Number(d.Pivo) || 0) + (Number(d.Vino) || 0) + (Number(d.Panaky) || 0);
  if (total <= 0) return;

  const at = d.DrinkAt?.toMillis ? d.DrinkAt.toMillis() : Date.now();
  const sessionRef = db.doc(`DrinkSessions/${d.UserId}`);
  let newSession = false;
  await db.runTransaction(async (tx) => {
    const s = await tx.get(sessionRef);
    const last = s.exists ? s.get("LastAt")?.toMillis?.() ?? 0 : 0;
    if (!s.exists || at - last > SESSION_GAP_MS) {
      newSession = true;
      tx.set(sessionRef, { LastAt: Timestamp.fromMillis(at), SessionId: event.params.id, StartedAt: Timestamp.fromMillis(at) });
    } else if (at > last) {
      tx.update(sessionRef, { LastAt: Timestamp.fromMillis(at) });
    }
  });
  if (!newSession) return;

  const userRef = db.doc(`Users/${d.UserId}`);
  await userRef.set({ SessionId: event.params.id, SessionStartedAt: Timestamp.fromMillis(at) }, { merge: true });

  // hlásit? nové verze posílají ShareDrink, starší jen SharePub (= hlásil hospodu)
  const share = d.ShareDrink === true || (d.ShareDrink === undefined && d.SharePub === true);
  if (!share) return;

  const friends = await friendsOf(d.UserId);
  if (friends.length === 0) return;

  const user = await userRef.get();
  const nick = d.Name || user.get("Nickname") || "Někdo";
  const gender = user.get("Gender");
  const dal = gender === "Píčka" ? "dala" : gender === "Píč" ? "dal" : "dal(a)";
  const sNim = gender === "Píčka" ? " s ní" : gender === "Píč" ? " s ním" : "";
  const where = d.SharePub === true && d.PubName ? `${d.PubName} – ` : "";

  await notifyData(friends, {
    type: "drink",
    title: `${nick} si právě ${dal} ${whatDrank(d)}`,
    body: `${where}připij si${sNim}! 🍻`,
    fromUid: d.UserId,
    sessionId: event.params.id,
    pubId: d.SharePub === true ? d.PubId : undefined,
  });
});

// ---------------- připíjení: „Petr si s tebou připíjí 🍺“ ----------------

exports.cheer = onDocumentCreated("Cheers/{id}", async (event) => {
  const c = event.data?.data();
  if (!c || !c.To || !c.From) return;
  const from = c.FromNick || (await nickOf(c.From));
  await notifyData([c.To], {
    type: "cheer",
    title: `${from} si s tebou připíjí ${c.Emoji || "🍻"}`,
    body: "Na zdraví! 🍻",
    fromUid: c.From,
  });
});

// ---------------- pozvánka: „Petr tě zve na drink“ (telefon sám pozná, jestli je do 15 km) ----------------

exports.invite = onDocumentCreated("Invites/{id}", async (event) => {
  const v = event.data?.data();
  if (!v || !v.From) return;
  const friends = await friendsOf(v.From);
  if (friends.length === 0) return;
  const from = v.FromNick || (await nickOf(v.From));
  await notifyData(friends, {
    type: "invite",
    title: `${from} tě zve na drink`,
    body: `${v.PubName || "Do hospody"} – přidáš se? 🍻`,
    fromUid: v.From,
    pubId: v.PubId,
    lat: v.Lat,
    lon: v.Lon,
  });
});

// ---------------- spotřeba (jen admin) ----------------
//
// Čísla bere z Cloud Monitoringu – stejná evidence, podle které by Google účtoval.
// Denní limity Firestore se nulují o půlnoci pacifického času (u nás 9:00), měsíc taky podle Pacifiku.

const ADMIN_UID = defineString("ADMIN_UID", { description: "UID účtu, který smí vidět spotřebu" });

const LIMITS = {
  reads: 50000,          // Firestore čtení / den
  writes: 20000,         // Firestore zápisy / den
  deletes: 20000,        // Firestore mazání / den
  invocations: 2000000,  // volání funkcí (Cloud Run požadavky) / měsíc
};

const METRICS = {
  reads: 'metric.type="firestore.googleapis.com/document/read_count"',
  writes: 'metric.type="firestore.googleapis.com/document/write_count"',
  deletes: 'metric.type="firestore.googleapis.com/document/delete_count"',
  // Cloud Functions 2. generace běží na Cloud Run → každé volání = jeden požadavek
  invocations: 'metric.type="run.googleapis.com/request_count" AND resource.type="cloud_run_revision"',
};

let metricClient;
function client() {
  if (!metricClient) metricClient = new monitoring.MetricServiceClient();
  return metricClient;
}

function projectId() {
  return process.env.GCLOUD_PROJECT || process.env.GOOGLE_CLOUD_PROJECT || JSON.parse(process.env.FIREBASE_CONFIG || "{}").projectId;
}

/** Posun pacifického času vůči UTC v minutách (−480 / −420) pro daný okamžik. */
function laOffsetMinutes(date) {
  const la = new Date(date.toLocaleString("en-US", { timeZone: "America/Los_Angeles" }));
  const utc = new Date(date.toLocaleString("en-US", { timeZone: "UTC" }));
  return Math.round((la - utc) / 60000);
}

/** Datum v Pacifiku (rok, měsíc 1–12, den). */
function laDate(date) {
  const parts = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Los_Angeles", year: "numeric", month: "2-digit", day: "2-digit" })
    .formatToParts(date).reduce((a, p) => ({ ...a, [p.type]: p.value }), {});
  return { y: +parts.year, m: +parts.month, d: +parts.day };
}

/** Okamžik (UTC) pacifické půlnoci daného pacifického data. */
function laMidnight(y, m, d) {
  const guess = new Date(Date.UTC(y, m - 1, d, 8));          // přibližně
  return new Date(Date.UTC(y, m - 1, d) - laOffsetMinutes(guess) * 60000);
}

const isoDay = ({ y, m, d }) => `${y}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;

/** Součet metriky v intervalu [from, to) jako jedno číslo. */
async function sumBetween(filter, from, to) {
  const seconds = Math.max(60, Math.floor((to - from) / 1000));
  const [series] = await client().listTimeSeries({
    name: client().projectPath(projectId()),
    filter,
    interval: { startTime: { seconds: Math.floor(from / 1000) }, endTime: { seconds: Math.floor(to / 1000) } },
    aggregation: {
      alignmentPeriod: { seconds },
      perSeriesAligner: "ALIGN_SUM",
      crossSeriesReducer: "REDUCE_SUM",
    },
    view: "FULL",
  });
  let total = 0;
  for (const ts of series) for (const p of ts.points || []) total += Number(p.value?.int64Value ?? p.value?.doubleValue ?? 0);
  return Math.round(total);
}

/** Hodinové součty → součty po pacifických dnech (posledních `days` dní). */
async function perDay(filter, days, now) {
  const from = now.getTime() - days * 86400000;
  const [series] = await client().listTimeSeries({
    name: client().projectPath(projectId()),
    filter,
    interval: { startTime: { seconds: Math.floor(from / 1000) }, endTime: { seconds: Math.floor(now.getTime() / 1000) } },
    aggregation: { alignmentPeriod: { seconds: 3600 }, perSeriesAligner: "ALIGN_SUM", crossSeriesReducer: "REDUCE_SUM" },
    view: "FULL",
  });
  const byDay = {};
  for (const ts of series) {
    for (const p of ts.points || []) {
      const end = Number(p.interval?.endTime?.seconds ?? 0) * 1000;
      const key = isoDay(laDate(new Date(end - 1000)));
      byDay[key] = (byDay[key] || 0) + Number(p.value?.int64Value ?? p.value?.doubleValue ?? 0);
    }
  }
  return byDay;
}

let usageCache = null; // { at, data } – ať opakované otevření během pár minut nevolá Monitoring znovu

/** Kompletní přehled spotřeby. */
async function computeUsage() {
  if (usageCache && Date.now() - usageCache.at < 2 * 60000) return usageCache.data;

  const now = new Date();
  const t = laDate(now);
  const dayStart = laMidnight(t.y, t.m, t.d).getTime();
  const monthStart = laMidnight(t.y, t.m, 1).getTime();
  const nextMonth = t.m === 12 ? laMidnight(t.y + 1, 1, 1) : laMidnight(t.y, t.m + 1, 1);
  const end = now.getTime();

  const [reads, writes, deletes, invocations, readsDays, writesDays, deletesDays] = await Promise.all([
    sumBetween(METRICS.reads, dayStart, end),
    sumBetween(METRICS.writes, dayStart, end),
    sumBetween(METRICS.deletes, dayStart, end),
    sumBetween(METRICS.invocations, monthStart, end),
    perDay(METRICS.reads, 30, now),
    perDay(METRICS.writes, 30, now),
    perDay(METRICS.deletes, 30, now),
  ]);

  // posledních 30 pacifických dní (dnešek přepsat přesným součtem)
  const days = [];
  for (let i = 29; i >= 0; i--) {
    const key = isoDay(laDate(new Date(dayStart - i * 86400000 + 12 * 3600000)));
    days.push({
      day: key,
      reads: Math.round(readsDays[key] || 0),
      writes: Math.round(writesDays[key] || 0),
      deletes: Math.round(deletesDays[key] || 0),
    });
  }
  days[days.length - 1] = { day: days[days.length - 1].day, reads, writes, deletes };

  const monthElapsed = Math.max(1, end - monthStart);
  const monthLength = nextMonth.getTime() - monthStart;

  const data = {
    limits: LIMITS,
    today: { reads, writes, deletes, since: new Date(dayStart).toISOString() },
    month: {
      invocations,
      since: new Date(monthStart).toISOString(),
      projected: Math.round(invocations * monthLength / monthElapsed),
    },
    days,
    updatedAt: now.toISOString(),
  };
  usageCache = { at: Date.now(), data };
  return data;
}

/** Appka (jen admin): přehled spotřeby. */
exports.usage = onCall(async (request) => {
  const admin = ADMIN_UID.value();
  if (!request.auth || !admin || request.auth.uid !== admin) {
    throw new HttpsError("permission-denied", "Spotřebu vidí jen správce.");
  }
  try {
    return await computeUsage();
  } catch (e) {
    logger.error("Spotřeba se nepodařila zjistit", e);
    throw new HttpsError("internal", `Monitoring neodpověděl: ${e.message}`);
  }
});

/**
 * Každou hodinu: blíží se limit? → oznámení adminovi (každá hranice max. jednou za pacifický den / měsíc).
 * Cloud Scheduler dává 3 úlohy měsíčně zdarma, tohle je jedna. Plánovač ve Frankfurtu (v Berlíně ho Google nemusí mít),
 * na umístění dat to nemá vliv – čte se jen Monitoring.
 */
exports.usageWatch = onSchedule({ schedule: "every 60 minutes", region: "europe-west3", timeZone: "Europe/Prague" }, async () => {
  const admin = ADMIN_UID.value();
  if (!admin) return;

  usageCache = null;
  const u = await computeUsage();
  const today = u.days[u.days.length - 1].day;
  const month = today.slice(0, 7);

  const checks = [
    { key: "reads", label: "Čtení z databáze", value: u.today.reads, limit: LIMITS.reads, period: today, unit: "dnes" },
    { key: "writes", label: "Zápisy do databáze", value: u.today.writes, limit: LIMITS.writes, period: today, unit: "dnes" },
    { key: "deletes", label: "Mazání v databázi", value: u.today.deletes, limit: LIMITS.deletes, period: today, unit: "dnes" },
    { key: "invocations", label: "Volání funkcí", value: u.month.invocations, limit: LIMITS.invocations, period: month, unit: "tento měsíc" },
  ];

  const stateRef = db.doc("Stats/usageAlerts");
  const state = (await stateRef.get()).data() || {};
  const updates = {};
  const lines = [];

  for (const c of checks) {
    const pct = c.value / c.limit;
    const level = pct >= 1 ? 100 : pct >= 0.8 ? 80 : 0;
    if (level === 0) continue;
    const sentKey = `${c.key}_${c.period}`;
    if ((state[sentKey] || 0) >= level) continue;   // tuhle hranici už dnes/tento měsíc hlásil
    updates[sentKey] = level;
    lines.push(`${c.label}: ${Math.round(pct * 100)} % (${c.value.toLocaleString("cs-CZ")} z ${c.limit.toLocaleString("cs-CZ")} ${c.unit})`);
  }
  if (lines.length === 0) return;

  await stateRef.set(updates, { merge: true });
  const tokens = await tokensOf([admin]);
  if (tokens.length === 0) return;
  await getMessaging().sendEachForMulticast({
    tokens: tokens.map((t) => t.token),
    notification: { title: "Dezoláti: blíží se limit", body: lines.join("\n") },
    data: { title: "Dezoláti: blíží se limit", body: lines.join("\n"), type: "usage" },
    android: { priority: "high", notification: { channelId: "kamosi", icon: "ic_stat_beer", color: "#6B3E1E" } },
  });
  logger.warn("Upozornění na limit", lines);
});
