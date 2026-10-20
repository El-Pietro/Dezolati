/**
 * Dezoláti – Cloud Functions (Firebase Blaze, reálně 0 Kč: 2 mil. volání měsíčně zdarma).
 *
 * Posílá oznámení na telefony kámošů (FCM). Appka sama posílat nesmí – musela by mít servisní klíč.
 *  - friendRequest:  nová žádost o přátelství  → adresátovi
 *  - friendAccepted: žádost přijata            → žadateli
 *  - atThePub:       první pivo v hospodě dne  → všem kámošům („Petr sedí v U Peťky“), jen při Drinks.SharePub
 *
 * Tokeny telefonů: PushTokens/{hash} { Uid, Token }. Kdo nechce: Users/{uid}.NotifyFriends == false.
 * Nasazení: firebase deploy --only functions   (viz functions/README.md)
 */
const { onDocumentCreated, onDocumentUpdated } = require("firebase-functions/v2/firestore");
const { setGlobalOptions, logger } = require("firebase-functions/v2");
const { initializeApp } = require("firebase-admin/app");
const { getFirestore, FieldValue } = require("firebase-admin/firestore");
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

// ---------------- „sedí v hospodě“ ----------------

exports.atThePub = onDocumentCreated("Drinks/{id}", async (event) => {
  const d = event.data?.data();
  if (!d || !d.PubId || d.Correction || !d.UserId) return;
  // hospodu si teď appka ukládá i pro statistiky; kámošům jen když ji hlásí
  // (starší verze appky SharePub neposílaly a PubId měly jen při hlášení → undefined = hlásí)
  if (d.SharePub === false) return;

  // jen první zápis v té hospodě za hospodský den (další piva nikoho nespamují)
  const key = `${d.UserId}_${d.Day || "x"}_${d.PubId}`;
  try {
    await db.doc(`PubPresence/${key}`).create({
      UserId: d.UserId, PubId: d.PubId, PubName: d.PubName || null, Day: d.Day || null,
      At: FieldValue.serverTimestamp(),
    });
  } catch (e) {
    if (e.code === 6 /* ALREADY_EXISTS */) return;
    throw e;
  }

  // jen array-contains (bez dalšího filtru) → není potřeba složený index; nepřijaté žádosti se odfiltrují tady
  const friends = await db.collection("Friends").where("Members", "array-contains", d.UserId).get();
  const others = friends.docs
    .filter((f) => f.get("Status") === "Accepted")
    .map((f) => (f.get("UserId") === d.UserId ? f.get("FriendId") : f.get("UserId")));
  if (others.length === 0) return;

  const nick = d.Name || (await nickOf(d.UserId));
  const pub = d.PubName || "Někde poblíž";
  await notify(others, `${nick} sedí v hospodě`, `${pub} – přidáš se? 🍻`, { type: "pub", pubId: String(d.PubId) });
});
