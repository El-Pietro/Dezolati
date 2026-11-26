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
 *  - osmReport:      návrh poznámky do OpenStreetMap (zavřeno / otevřeno / nový podnik) → adminovi ke schválení
 *  - deleteAccount:  smazání účtu a všech dat uživatele (appka i web, kvůli Google Play)
 *  - inviteAction:   pozvánky na vybrané kámoše (založit, Jdu / Nejdu, pozvat další, přidat se)
 *  - alkoholikDne:   v 7:00 po pátku, sobotě, svátku a dni před svátkem → top 3 mezi kámoši
 *  - akce u kámoše (akce.js): akceAction, akceDrink, akceResults → Akce/{id} (akční lístek, bečky, výsledky)
 *  - eventy (vanoce.js): eventsSync, badgesDrink, badgesCheer, badgesInvite, badgesSuchyStart, trhReport, trhAction
 *                    → vánoční trhy (Events/aktualni) a odznaky (Achievements/{uid})
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
const { getAuth } = require("firebase-admin/auth");

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

// druh panáku (od 3.12, Services/PanakDruhy.cs) → 4. pád: [1, 2–4, 5+]
const PANAK_DRUHY = {
  Slivovice: ["slivovici", "slivovice", "slivovic"],
  Liker: ["likér", "likéry", "likérů"],
  Rum: ["rum", "rumy", "rumů"],
  Vodka: ["vodku", "vodky", "vodek"],
  Whisky: ["whisky", "whisky", "whisky"],
};

/** „pivo“, „2 piva“, „víno a slivovici“, „míchačku“ … (4. pád). */
function whatDrank(d) {
  const n = (x) => Math.max(0, Number(x) || 0);
  const form = (k, one, few, many) => (k === 1 ? one : k >= 2 && k <= 4 ? `${k} ${few}` : `${k} ${many}`);
  const parts = [];
  if (n(d.Pivo) > 0) parts.push(form(n(d.Pivo), "pivo", "piva", "piv"));
  if (n(d.Vino) > 0) parts.push(form(n(d.Vino), "víno", "vína", "vín"));
  if (n(d.Panaky) > 0) parts.push(form(n(d.Panaky), ...(PANAK_DRUHY[d.PanakDruh] || ["panáka", "panáky", "panáků"])));
  if (n(d.Michacky) > 0) parts.push(form(n(d.Michacky), "míchačku", "míchačky", "míchaček"));
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
  const total = (Number(d.Pivo) || 0) + (Number(d.Vino) || 0) + (Number(d.Panaky) || 0) + (Number(d.Michacky) || 0);
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

  const user = await userRef.get();
  // tajný režim: kámoši v MaskFor se o pití nedozví
  const masked = new Set(user.get("MaskFor") || []);
  const friends = (await friendsOf(d.UserId)).filter((f) => !masked.has(f));
  if (friends.length === 0) return;

  const nick = user.get("Nickname") || d.Name || "Někdo";   // přezdívka, ne jméno
  const gender = user.get("Gender");
  const dal = gender === "Píčka" ? "dala" : gender === "Píč" ? "dal" : "dal(a)";
  const sNim = gender === "Píčka" ? " s ní" : gender === "Píč" ? " s ním" : "";
  // „I kde“ (SharePub): hospoda / doma / venku
  const where = d.SharePub !== true ? ""
    : d.PubName ? ` – ${d.PubName}`
    : d.Place === "home" ? " doma"
    : d.Place === "other" ? " venku" : "";

  const data = {
    type: "drink",
    title: `${nick} si právě ${dal} ${whatDrank(d)}${where}`,
    body: `Připij si${sNim}! 🍻`,
    fromUid: d.UserId,
    fromNick: nick,            // appka ho nahradí vlastní přezdívkou kámoše, když nějakou má
    sessionId: event.params.id,
    pubId: d.SharePub === true ? d.PubId : undefined,
  };

  // lidi ze stejné běžící pozvánky (on jde, příjemce neodmítl) → appka je sloučí do oznámení té pozvánky
  const byInvite = new Map();   // inviteId → { name, uids }
  const plain = new Set(friends);
  const invites = await db.collection("Invites").where("Members", "array-contains", d.UserId)
    .where("EndsAt", ">", Timestamp.now()).get();
  for (const inv of invites.docs) {
    const v = inv.data();
    if (v.Version !== 2 || v.Status?.[d.UserId] !== "yes") continue;
    for (const f of friends) {
      if (!plain.has(f) || !(v.Members || []).includes(f) || v.Status?.[f] === "no") continue;
      if (!byInvite.has(inv.id)) byInvite.set(inv.id, { name: v.PubName || "Pozvánka", uids: [] });
      byInvite.get(inv.id).uids.push(f);
      plain.delete(f);
    }
  }
  await notifyData([...plain], data);
  for (const [id, g] of byInvite) {
    await notifyData(g.uids, { ...data, inviteId: id, inviteName: g.name });
  }
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
    fromNick: from,            // appka si připití připíše rovnou (bez čtení) a spojí oznámení
    emoji: c.Emoji || "🍻",
    sessionId: c.SessionId,
  });
});

// ---------------- pozvánka: „Petr tě zve na drink“ (telefon sám pozná, jestli je do 15 km) ----------------

exports.invite = onDocumentCreated("Invites/{id}", async (event) => {
  const v = event.data?.data();
  if (!v || !v.From) return;
  if (v.Version === 2) return;   // nové pozvánky (vybraní kámoši) zakládá a rozesílá inviteAction
  const inviter = await db.doc(`Users/${v.From}`).get();
  const masked = new Set((inviter.exists && inviter.get("MaskFor")) || []);   // tajný režim
  const friends = (await friendsOf(v.From)).filter((f) => !masked.has(f));
  if (friends.length === 0) return;
  const from = v.FromNick || (inviter.exists && inviter.get("Nickname")) || "Někdo";
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

// ---------------- návrh poznámky do OSM → adminovi ke schválení (Nastavení → Návrhy do OSM) ----------------

exports.osmReport = onDocumentCreated("OsmReports/{id}", async (event) => {
  const r = event.data?.data();
  const admin = ADMIN_UID.value();
  if (!r || !admin) return;
  const what = { closed: "zavřeno", reopened: "zase otevřeno", added: "nový podnik" }[r.What] || r.What;
  await notifyData([admin], {
    type: "osm",
    title: `Návrh do OSM: ${r.Name || "?"} – ${what}`,
    body: `Od ${r.ByNick || "?"}. Schválíš v Nastavení → Návrhy do OSM.`,
  });
});

// ---------------- smazání účtu (appka i web; Google Play to vyžaduje) ----------------
//
// Maže všechno, co patří uživateli: profil, soukromé věci, pití (log i denní součty), přátelství, tokeny telefonů,
// připíjení, pozvánky, statistiku hospod, sezení, řádek v adresáři – a nakonec účet ve Firebase Auth.
// Úpravy hospod (Pubs / PubEdits) a návrhy do OSM zůstávají – jsou to společná data o hospodách, ne o člověku;
// přezdívka se z nich smaže.
// Z appky by to nešlo: Drinks smí klient jen vytvářet a cizí dokumenty (přátelství, připíjení) mazat nesmí celé.

async function deleteQuery(query) {
  let n = 0;
  for (;;) {
    const snap = await query.limit(400).get();
    if (snap.empty) return n;
    const batch = db.batch();
    snap.docs.forEach((d) => batch.delete(d.ref));
    await batch.commit();
    n += snap.size;
    if (snap.size < 400) return n;
  }
}

async function anonymize(query, fields) {
  for (;;) {
    const snap = await query.limit(400).get();
    if (snap.empty) return;
    const batch = db.batch();
    snap.docs.forEach((d) => batch.update(d.ref, fields));
    await batch.commit();
    if (snap.size < 400) return;
  }
}

exports.deleteAccount = onCall(async (request) => {
  const uid = request.auth?.uid;
  if (!uid) throw new HttpsError("unauthenticated", "Nejdřív se přihlas.");
  if (request.data?.confirm !== "SMAZAT") throw new HttpsError("failed-precondition", "Chybí potvrzení.");

  const counts = {};
  try {
    counts.drinks = await deleteQuery(db.collection("Drinks").where("UserId", "==", uid));
    counts.daily = await deleteQuery(db.collection("DailyDrinks").where("UserId", "==", uid));
    counts.friends = await deleteQuery(db.collection("Friends").where("Members", "array-contains", uid));
    counts.tokens = await deleteQuery(db.collection("PushTokens").where("Uid", "==", uid));
    counts.cheersFrom = await deleteQuery(db.collection("Cheers").where("From", "==", uid));
    counts.cheersTo = await deleteQuery(db.collection("Cheers").where("To", "==", uid));
    counts.invites = await deleteQuery(db.collection("Invites").where("From", "==", uid));
    counts.trhReports = await deleteQuery(db.collection("TrhReports").where("By", "==", uid));

    // společná data o hospodách nechat, jen bez přezdívky
    await anonymize(db.collection("Pubs").where("UpdatedBy", "==", uid), { UpdatedByNick: "(smazaný účet)" });
    await anonymize(db.collection("Pubs").where("CreatedBy", "==", uid), { CreatedByNick: "(smazaný účet)" });
    await anonymize(db.collection("PubEdits").where("By", "==", uid), { ByNick: "(smazaný účet)" });
    await anonymize(db.collection("OsmReports").where("By", "==", uid), { ByNick: "(smazaný účet)" });

    await db.doc("Directory/users").update({ [uid]: FieldValue.delete() }).catch(() => {});
    for (const path of [`Users/${uid}`, `Private/${uid}`, `UserPubStats/${uid}`, `DrinkSessions/${uid}`,
      `Achievements/${uid}`, `AchievementState/${uid}`]) {
      await db.doc(path).delete();
    }
    await getAuth().deleteUser(uid);
  } catch (e) {
    logger.error(`Smazání účtu ${uid} se nepovedlo`, e, counts);
    throw new HttpsError("internal", `Smazání se nedokončilo: ${e.message}. Zkus to znovu – co už je pryč, pryč zůstane.`);
  }
  logger.info(`Účet ${uid} smazán`, counts);
  return { ok: true, ...counts };
});

// ---------------- Alkoholik dne: ráno v 7:00 top 3 mezi kámoši ----------------
//
// Jen po „hospodských“ nocích: v sobotu a v neděli ráno, ráno po svátku a ráno po dni před svátkem
// (Silvestr → 1. 1. ráno, Štědrý den …). Každý dostane pořadí svého okruhu (on + kámoši) za včerejší tah.
// Kdo nehlásí, že pije (ShareLevel 0), v cizím pořadí není; v tajném režimu (MaskFor) je jen třetina.
// Čtení: denní součty za včerejšek (jen kdo pil) + přátelství + profily těch, kdo pili.

/** Velikonoční neděle (gregoriánský kalendář, Meeus/Jones/Butcher). */
function easterSunday(y) {
  const a = y % 19, b = Math.floor(y / 100), c = y % 100, d = Math.floor(b / 4), e = b % 4;
  const f = Math.floor((b + 8) / 25), g = Math.floor((b - f + 1) / 3);
  const h = (19 * a + b - d - g + 15) % 30, i = Math.floor(c / 4), k = c % 4;
  const l = (32 + 2 * e + 2 * i - h - k) % 7, m = Math.floor((a + 11 * h + 22 * l) / 451);
  const month = Math.floor((h + l - 7 * m + 114) / 31), day = ((h + l - 7 * m + 114) % 31) + 1;
  return new Date(Date.UTC(y, month - 1, day));
}

/** Státní svátky a ostatní svátky ČR (dny pracovního klidu) jako „YYYY-MM-DD“. */
function czHolidays(y) {
  const fixed = ["01-01", "05-01", "05-08", "07-05", "07-06", "09-28", "10-28", "11-17", "12-24", "12-25", "12-26"]
    .map((md) => `${y}-${md}`);
  const easter = easterSunday(y);
  const shift = (days) => new Date(easter.getTime() + days * 86400000).toISOString().slice(0, 10);
  return new Set([...fixed, shift(-2), shift(1)]);   // Velký pátek, Velikonoční pondělí
}

/** Dnešní datum v Praze jako {y, m, d} (UTC Date o půlnoci). */
function pragueToday() {
  const s = new Intl.DateTimeFormat("en-CA", { timeZone: "Europe/Prague", year: "numeric", month: "2-digit", day: "2-digit" })
    .format(new Date());
  return new Date(`${s}T00:00:00Z`);
}

const isoOf = (date) => date.toISOString().slice(0, 10);
const addDays = (date, n) => new Date(date.getTime() + n * 86400000);

/** Byl včerejšek „hospodský“? (pátek, sobota, svátek, den před svátkem) */
function wasPartyNight(today) {
  const y = addDays(today, -1);
  const dow = y.getUTCDay();   // 5 = pátek, 6 = sobota
  if (dow === 5 || dow === 6) return true;
  const hol = new Set([...czHolidays(y.getUTCFullYear()), ...czHolidays(y.getUTCFullYear() + 1)]);
  return hol.has(isoOf(y)) || hol.has(isoOf(today));   // včera svátek, nebo dnes svátek = včera den před ním
}

exports.alkoholikDne = onSchedule({ schedule: "0 7 * * *", region: "europe-west3", timeZone: "Europe/Prague" }, async () => {
  const today = pragueToday();
  if (!wasPartyNight(today)) return;
  const day = isoOf(addDays(today, -1));

  // kdo včera pil (Day = hospodský den, tah do 6:00 se počítá ke včerejšku)
  const daily = await db.collection("DailyDrinks").where("Day", "==", day).get();
  const totals = new Map();
  const masked = new Map();   // tajný režim: stejná třetina jako v appce (Services/Mask.cs)
  for (const d of daily.docs) {
    const p = Math.max(0, Number(d.get("Pivo")) || 0), v = Math.max(0, Number(d.get("Vino")) || 0),
      s = Math.max(0, Number(d.get("Panaky")) || 0) + Math.max(0, Number(d.get("Michacky")) || 0);
    const t = p + v + s;
    if (t > 0 && d.get("UserId")) {
      totals.set(d.get("UserId"), t);
      masked.set(d.get("UserId"), Math.max(1, Math.round((p + s) / 3) + Math.round(v / 3)));
    }
  }
  if (totals.size < 2) return;

  // profily pijících: přezdívka, sdílení, tajný režim
  const drinkers = new Map();
  const ids = [...totals.keys()];
  for (let i = 0; i < ids.length; i += 100) {
    const refs = ids.slice(i, i + 100).map((id) => db.doc(`Users/${id}`));
    for (const u of await db.getAll(...refs)) {
      if (!u.exists) continue;
      drinkers.set(u.id, {
        nick: u.get("Nickname") || "?",
        share: u.get("ShareLevel") ?? 2,
        mask: new Set(u.get("MaskFor") || []),
      });
    }
  }

  // okruh každého, kdo by oznámení mohl dostat: on + přijatí kámoši
  const friendsSnap = await db.collection("Friends").where("Status", "==", "Accepted").get();
  const circles = new Map();
  const link = (a, b) => { if (!circles.has(a)) circles.set(a, new Set()); circles.get(a).add(b); };
  for (const f of friendsSnap.docs) {
    const a = f.get("UserId"), b = f.get("FriendId");
    if (!a || !b) continue;
    link(a, b);
    link(b, a);
  }

  const medals = ["🥇", "🥈", "🥉"];
  let sent = 0;
  for (const [uid, friends] of circles) {
    const ranking = [...friends, uid]
      .filter((id) => totals.has(id) && drinkers.has(id))
      .filter((id) => id === uid || drinkers.get(id).share !== 0)
      .map((id) => ({
        id,
        n: id !== uid && drinkers.get(id).mask.has(uid) ? masked.get(id) : totals.get(id),
        nick: id === uid ? "ty" : drinkers.get(id).nick,
      }))
      .sort((x, y) => y.n - x.n);
    if (ranking.length < 2) continue;   // sám se sebou se nesoutěží

    const top = ranking.slice(0, 3);
    const winner = top[0];
    const title = winner.id === uid ? "Alkoholik dne: TY 🏆" : `Alkoholik dne: ${winner.nick} 🏆`;
    const body = top.map((r, i) => `${medals[i]} ${r.nick} – ${r.n}`).join("   ");
    await notify([uid], title, body, { type: "daily" });
    sent++;
  }
  logger.info(`Alkoholik dne ${day}: ${totals.size} pijících, ${sent} oznámení`);
});

// ---------------- pozvánky na vybrané kámoše (od verze 3.11) ----------------
//
// Invites/{id} { Version: 2, From, FromNick, PubId, PubName, Lat, Lon, At, EndsAt (ráno 6:00),
//   Members: [uid…], Status: { uid: "yes" | "no" | "wait" }, Nicks: { uid: přezdívka }, InvitedBy: { uid: kdo ho pozval } }
// Číst smí jen členové (pravidla), měnit jen server – tady se hlídá, že zvát jde jen vlastní kámoše.
// „Přidat se“: k cizí pozvánce do stejné hospody, když v ní je (a jde) někdo z mých kámošů.

const MAX_INVITE_MEMBERS = 40;

/** Nejbližší 6:00 ráno pražského času (pozvánka končí s hospodským dnem). */
function nextSixAm() {
  const now = new Date();
  const fmt = new Intl.DateTimeFormat("en-GB", { timeZone: "Europe/Prague", hour: "2-digit", hourCycle: "h23" });
  const hour = Number(fmt.format(now));
  const today = pragueToday();
  const day = hour < 6 ? today : addDays(today, 1);
  // 6:00 Praha = 4:00 nebo 5:00 UTC podle letního času: zkusit obě, vzít tu, která v Praze dává 6
  for (const utcHour of [4, 5]) {
    const t = new Date(day.getTime() + utcHour * 3600000);
    if (Number(fmt.format(t)) === 6) return t;
  }
  return new Date(day.getTime() + 5 * 3600000);
}

async function nicksOf(uids) {
  const out = {};
  const refs = uids.map((u) => db.doc(`Users/${u}`));
  for (let i = 0; i < refs.length; i += 100) {
    for (const d of await db.getAll(...refs.slice(i, i + 100))) out[d.id] = (d.exists && d.get("Nickname")) || "?";
  }
  return out;
}

function inviteData(id, v) {
  return { inviteId: id, pubId: v.PubId, type: "invite" };
}

exports.inviteAction = onCall(async (request) => {
  const me = request.auth?.uid;
  if (!me) throw new HttpsError("unauthenticated", "Nejdřív se přihlas.");
  const a = request.data || {};
  const myFriends = new Set(await friendsOf(me));
  const myNick = (await nicksOf([me]))[me];

  // ----- založit -----
  if (a.action === "create") {
    const members = [...new Set((a.members || []).filter((u) => typeof u === "string"))];
    if (members.length === 0) throw new HttpsError("invalid-argument", "Nikoho jsi nevybral.");
    if (members.length > MAX_INVITE_MEMBERS) throw new HttpsError("invalid-argument", "Tolik lidí se do hospody nevejde.");
    if (members.some((u) => !myFriends.has(u))) throw new HttpsError("permission-denied", "Zvát jde jen kámoše.");
    if (typeof a.pubId !== "string" || typeof a.pubName !== "string" || a.pubName.length > 120)
      throw new HttpsError("invalid-argument", "Chybí hospoda.");

    const nicks = await nicksOf(members);
    nicks[me] = myNick;
    const status = { [me]: "yes" };
    const invitedBy = {};
    for (const u of members) { status[u] = "wait"; invitedBy[u] = me; }
    const ref = db.collection("Invites").doc();
    const v = {
      Version: 2, From: me, FromNick: myNick, PubId: a.pubId, PubName: a.pubName,
      Lat: Number(a.lat) || 0, Lon: Number(a.lon) || 0,
      At: FieldValue.serverTimestamp(), EndsAt: Timestamp.fromDate(nextSixAm()),
      Members: [me, ...members], Status: status, Nicks: nicks, InvitedBy: invitedBy,
    };
    await ref.set(v);

    // úklid: moje pozvánky starší než 2 dny
    const old = await db.collection("Invites").where("From", "==", me)
      .where("EndsAt", "<", Timestamp.fromMillis(Date.now() - 2 * 86400000)).limit(50).get();
    await Promise.all(old.docs.map((d) => d.ref.delete()));

    await notifyData(members, {
      ...inviteData(ref.id, v),
      title: `${myNick} tě zve do hospody`,
      body: `${a.pubName} – jdeš? 🍻`,
      fromUid: me,
    });
    return { id: ref.id };
  }

  // ----- hospoda: pozvánky mých kámošů, ve kterých nejsem -----
  if (a.action === "atPub") {
    if (typeof a.pubId !== "string") return { invites: [] };
    const snap = await db.collection("Invites").where("PubId", "==", a.pubId)
      .where("EndsAt", ">", Timestamp.now()).get();
    const invites = [];
    for (const d of snap.docs) {
      const v = d.data();
      if (v.Version !== 2 || (v.Members || []).includes(me)) continue;
      const going = (v.Members || []).filter((u) => v.Status?.[u] === "yes");
      const friendsGoing = going.filter((u) => myFriends.has(u));
      if (friendsGoing.length === 0) continue;
      invites.push({ id: d.id, fromNick: v.FromNick, friends: friendsGoing.map((u) => v.Nicks?.[u] || "?"), going: going.length });
    }
    return { invites };
  }

  // ostatní akce pracují s jednou pozvánkou
  if (typeof a.id !== "string") throw new HttpsError("invalid-argument", "Chybí pozvánka.");
  const ref = db.doc(`Invites/${a.id}`);
  const doc = await ref.get();
  if (!doc.exists || doc.get("Version") !== 2) throw new HttpsError("not-found", "Pozvánka už neexistuje.");
  const v = doc.data();
  if (v.EndsAt?.toMillis?.() < Date.now()) throw new HttpsError("failed-precondition", "Pozvánka už skončila (v 6 ráno).");
  const members = v.Members || [];
  const isMember = members.includes(me);

  // ----- Jdu / Nejdu -----
  if (a.action === "respond") {
    if (!isMember) throw new HttpsError("permission-denied", "Do téhle pozvánky nepatříš.");
    const answer = a.answer === "yes" ? "yes" : "no";
    if (v.Status?.[me] === answer) return { ok: true };
    await ref.update({ [`Status.${me}`]: answer });
    const tell = [...new Set([v.From, v.InvitedBy?.[me]])].filter((u) => u && u !== me);
    if (answer === "yes") {
      await notifyData(tell, { ...inviteData(a.id, v), title: `${myNick} jde taky 🍻`, body: v.PubName || "", fromUid: me });
    } else {
      await notifyData(tell, { ...inviteData(a.id, v), title: `${myNick} dneska nedorazí`, body: v.PubName || "", fromUid: me });
    }
    return { ok: true };
  }

  // ----- pozvat další (zve kdokoli z pozvánky, kdo neřekl ne) -----
  if (a.action === "add") {
    if (!isMember || v.Status?.[me] === "no") throw new HttpsError("permission-denied", "Zvát můžou jen ti, kdo jdou.");
    const add = [...new Set((a.members || []).filter((u) => typeof u === "string" && !members.includes(u)))];
    if (add.length === 0) return { ok: true };
    if (add.some((u) => !myFriends.has(u))) throw new HttpsError("permission-denied", "Zvát jde jen vlastní kámoše.");
    if (members.length + add.length > MAX_INVITE_MEMBERS) throw new HttpsError("invalid-argument", "Tolik lidí se do hospody nevejde.");
    const nicks = await nicksOf(add);
    const update = { Members: FieldValue.arrayUnion(...add) };
    for (const u of add) {
      update[`Status.${u}`] = "wait";
      update[`Nicks.${u}`] = nicks[u];
      update[`InvitedBy.${u}`] = me;
    }
    await ref.update(update);
    const via = me === v.From ? "" : ` (pozvánka od ${v.FromNick})`;
    await notifyData(add, {
      ...inviteData(a.id, v),
      title: `${myNick} tě zve do hospody`,
      body: `${v.PubName}${via} – jdeš? 🍻`,
      fromUid: me,
    });
    return { ok: true };
  }

  // ----- přidat se k pozvánce, kde je můj kámoš -----
  if (a.action === "join") {
    if (isMember) {
      await ref.update({ [`Status.${me}`]: "yes" });
      return { ok: true };
    }
    const friendIn = members.find((u) => myFriends.has(u) && v.Status?.[u] === "yes");
    if (!friendIn) throw new HttpsError("permission-denied", "Nikoho z téhle pozvánky neznáš.");
    if (members.length + 1 > MAX_INVITE_MEMBERS) throw new HttpsError("invalid-argument", "Tahle hospoda je plná.");
    await ref.update({
      Members: FieldValue.arrayUnion(me),
      [`Status.${me}`]: "yes",
      [`Nicks.${me}`]: myNick,
      [`InvitedBy.${me}`]: friendIn,
    });
    const tell = [...new Set([v.From, friendIn])].filter((u) => u !== me);
    await notifyData(tell, { ...inviteData(a.id, v), title: `${myNick} se přidává 🍻`, body: v.PubName || "", fromUid: me });
    return { ok: true };
  }

  throw new HttpsError("invalid-argument", "Neznámá akce.");
});

// ---------------- eventy (Vánoce 2026): trhy a odznaky – viz vanoce.js ----------------

const vanoce = require("./vanoce")({ db, notifyData, friendsOf, ADMIN_UID });
for (const [name, fn] of Object.entries(vanoce)) if (!name.startsWith("_")) exports[name] = fn;

// ---------------- akce u kámoše (3.14) – viz akce.js ----------------

const akce = require("./akce")({ db, notifyData, friendsOf });
for (const [name, fn] of Object.entries(akce)) if (!name.startsWith("_")) exports[name] = fn;
