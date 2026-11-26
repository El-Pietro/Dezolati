/**
 * Dezoláti – akce u kámoše (3.14): naplánovaný večírek mimo hospodu, akční lístek na pivo, bečky, výsledky.
 *
 *  Akce/{id}            – vše o akci v JEDNOM dokumentu (akční lístek = 1 čtení). Čte jen člen (Members), píše jen server.
 *     Name, From, To (Timestamp), Lat, Lon, Place, Host, Code, Theme (map), Members [uid], Status {uid: yes|maybe|no|wait},
 *     Nicks {klíč: jméno}, Guests {uid: true} (anonymní hosté z odkazu), Names {klíč: true} (lidé jen se jménem),
 *     Kegs [{Name, Liters, Beers, TappedAt, EmptyAt}], Keg (index naražené, -1 = žádná), Rows {klíč: {N, Last, By}},
 *     History [{By, At, What, Before}] (posledních 60), Version, Results (po konci), Deleted
 *  Akce/{id}/Log/{auto} – jednotlivé zápisy na akční lístek (detail člověka): Who, Delta, At, By, ByNick, Src
 *  Akce/{id}/Media/{slot} – vlastní obrázky výzdoby (base64, zmenšené v telefonu); čte člen
 *  AkceCodes/{kód}      – odkaz pro hosty → id akce (jen server)
 *
 *  Klíč řádku: uid (appka i anonymní host z odkazu) nebo „n_…“ (člověk jen se jménem, píšou mu ostatní).
 *  Pivo na akci: z akčního lístku (akceAction beer – za appkáře server zapíše i do jeho Drinks/DailyDrinks,
 *  AkceBy = kdo zapsal), nebo z běžného lístku v místě akce (appka dá do Drinks Akce = id → trigger akceDrink).
 *  Po každé změně tichý push se stavem (type akce) členům s appkou ≥ 3.14 – v appce 0 čtení.
 */
const { onDocumentCreated } = require("firebase-functions/v2/firestore");
const { onCall, HttpsError } = require("firebase-functions/v2/https");
const { onSchedule } = require("firebase-functions/v2/scheduler");
const { logger } = require("firebase-functions/v2");
const { Timestamp, FieldValue } = require("firebase-admin/firestore");
const { getMessaging } = require("firebase-admin/messaging");
const crypto = require("crypto");

module.exports = function akce({ db, notifyData, friendsOf }) {
  const out = {};
  const MAX_MEMBERS = 60;
  const BEER_L = 0.5;
  const MIN_VERSION = [3, 14];   // tichý push jen appkám, které ho umí (starší by ukázaly prázdné oznámení)

  // ---------------- pomocné ----------------

  const PF = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Europe/Prague", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  });
  function prague(date) {
    const p = Object.fromEntries(PF.formatToParts(date).map((x) => [x.type, x.value]));
    const iso = `${p.year}-${p.month}-${p.day}`;
    const h = Number(p.hour);
    const pub = h < 6 ? new Date(Date.parse(iso + "T00:00:00Z") - 86400000).toISOString().slice(0, 10) : iso;
    return { iso, pub, hm: `${p.hour}:${p.minute}` };
  }
  const ms = (t) => (t?.toMillis ? t.toMillis() : typeof t === "number" ? t : 0);
  const str = (v, max) => (typeof v === "string" ? v.trim().slice(0, max) : "");
  const num = (v) => (typeof v === "number" && isFinite(v) ? v : null);
  const ts = (v) => (typeof v === "number" && v > 0 ? Timestamp.fromMillis(v) : null);
  const kegBeers = (k) => Math.round((Number(k.Liters) || 0) / BEER_L);
  const rnd = (n) => crypto.randomBytes(n).toString("base64url").slice(0, n);

  const err = (code, msg) => new HttpsError(code, msg);
  const piv = (n) => `${n} ${n === 1 ? "pivo" : n >= 2 && n <= 4 ? "piva" : "piv"}`;

  async function nickOf(uid) {
    const d = await db.doc(`Users/${uid}`).get();
    return (d.exists && d.get("Nickname")) || "?";
  }

  /** Stav pro appku (akční lístek): jen to, co lístek potřebuje – vejde se do pushe (4 kB). */
  function state(id, a) {
    const k = a.Keg >= 0 ? a.Kegs?.[a.Keg] : null;
    return {
      id, v: a.Version || 0,
      n: a.Name, f: ms(a.From), t: ms(a.To),
      k: k ? { n: k.Name, l: k.Liters, b: k.Beers || 0, i: a.Keg, c: (a.Kegs || []).length, at: ms(k.TappedAt) } : null,
      r: Object.entries(a.Rows || {}).map(([key, r]) => [key, a.Nicks?.[key] || "?", r.N || 0, r.Last || 0, r.By || ""]),
    };
  }

  function versionOk(v) {
    const m = /^(\d+)\.(\d+)/.exec(v || "");
    return !!m && (Number(m[1]) > MIN_VERSION[0] || (Number(m[1]) === MIN_VERSION[0] && Number(m[2]) >= MIN_VERSION[1]));
  }

  /** Tichý push se stavem akce členům s appkou ≥ 3.14 (ne webům – prohlížeč by musel ukázat oznámení). */
  async function pushState(id, a, exceptUid) {
    const uids = (a.Members || []).filter((u) => u !== exceptUid && !(a.Guests || {})[u] && a.Status?.[u] !== "no");
    if (uids.length === 0) return;
    const dir = (await db.doc("Directory/users").get()).data() || {};
    const okUids = uids.filter((u) => versionOk(dir[u]?.V));
    if (okUids.length === 0) return;
    const tokens = [];
    for (let i = 0; i < okUids.length; i += 30) {
      const snap = await db.collection("PushTokens").where("Uid", "in", okUids.slice(i, i + 30)).get();
      snap.forEach((d) => { if (d.get("Platform") !== "Web" && d.get("Token")) tokens.push(d.get("Token")); });
    }
    if (tokens.length === 0) return;
    const payload = JSON.stringify(state(id, a));
    if (payload.length > 3500) logger.warn(`Akce ${id}: stav ${payload.length} B – blízko limitu pushe`);
    const res = await getMessaging().sendEachForMulticast({
      tokens, data: { type: "akce", akce: id, state: payload }, android: { priority: "high" },
    });
    logger.info(`Akce ${id}: stav → ${res.successCount}/${tokens.length}`);
  }

  async function load(id, uid, needYes = false) {
    if (typeof id !== "string" || !id) throw err("invalid-argument", "Chybí akce.");
    const ref = db.doc(`Akce/${id}`);
    const doc = await ref.get();
    if (!doc.exists || doc.get("Deleted")) throw err("not-found", "Akce už neexistuje.");
    const a = doc.data();
    if (!(a.Members || []).includes(uid)) throw err("permission-denied", "Do téhle akce nepatříš.");
    if (needYes && !["yes", "maybe"].includes(a.Status?.[uid])) throw err("permission-denied", "Nejdřív dej „Jdu“.");
    return { ref, a };
  }

  /** Zápis piva za appkáře do jeho lístku (Drinks + DailyDrinks + Users), stejné pole jako appka (Services/DrinkQueue.cs). */
  function drinkWrites(tx, uid, delta, by, byNick, akceId, at) {
    const now = new Date(at);
    const day = prague(now).pub;                       // hospodský den (do 6:00 včerejšek)
    const [y, m, d] = day.split("-");
    const key = `${d}-${m}-${y}`;
    const id = `akce_${akceId}_${now.getTime()}_${rnd(4)}`;
    tx.create(db.doc(`Drinks/${id}`), {
      UserId: uid, Name: null, Pivo: delta, Vino: 0, Panaky: 0, Michacky: 0, PanakDruh: null,
      DrinkAt: Timestamp.fromDate(now), Date: key, Day: day, Correction: delta < 0,
      PubId: null, PubName: null, SharePub: false, Place: "other", Kc: 0, ShareDrink: false,
      Akce: akceId, AkceBy: by, AkceByNick: byNick,
    });
    const inc = (v) => FieldValue.increment(v);
    tx.set(db.doc(`DailyDrinks/${uid}_${y}${m}${d}`), {
      UserId: uid, Date: key, Day: day, UpdatedAt: Timestamp.fromDate(now),
      Pivo: inc(delta), Vino: inc(0), Panaky: inc(0), Michacky: inc(0), Total: inc(delta), Kc: inc(0),
      PanakSlivovice: inc(0), PanakLiker: inc(0), PanakRum: inc(0), PanakVodka: inc(0), PanakWhisky: inc(0), PanakOstatni: inc(0),
      PlacePub: inc(0), PlaceHome: inc(0), PlaceOther: inc(delta),
    }, { merge: true });
    if (delta > 0) tx.set(db.doc(`Users/${uid}`), { LastDrinkAt: Timestamp.fromDate(now), LastDrinkDay: key, LastDrinkKind: "pivo" }, { merge: true });
  }

  /** +1 / −1 pivo na řádek akce (a bečku); vrací nový stav dokumentu. */
  function applyBeer(a, who, delta, byNick, at) {
    const rows = { ...(a.Rows || {}) };
    const r = { ...(rows[who] || { N: 0 }) };
    if (delta < 0 && (r.N || 0) <= 0) throw err("failed-precondition", "Není co škrtat.");
    r.N = (r.N || 0) + delta;
    if (delta > 0) { r.Last = at; r.By = byNick; }
    rows[who] = r;
    const kegs = (a.Kegs || []).map((k) => ({ ...k }));
    if (a.Keg >= 0 && kegs[a.Keg]) kegs[a.Keg].Beers = Math.max(0, (kegs[a.Keg].Beers || 0) + delta);
    return { ...a, Rows: rows, Kegs: kegs, Version: (a.Version || 0) + 1 };
  }

  function history(a, by, what, before) {
    const h = [...(a.History || []), { By: by, At: Timestamp.now(), What: what, Before: before ?? null }];
    return h.slice(-60);
  }

  // ---------------- akceAction ----------------

  out.akceAction = onCall(async (request) => {
    const me = request.auth?.uid;
    if (!me) throw err("unauthenticated", "Nejdřív se přihlas.");
    const anonymous = request.auth?.token?.firebase?.sign_in_provider === "anonymous";
    const x = request.data || {};
    const now = Date.now();

    // ----- host z odkazu: přidat se (i anonymní účet) -----
    if (x.action === "join") {
      const code = str(x.code, 20);
      const c = code ? await db.doc(`AkceCodes/${code}`).get() : null;
      if (!c?.exists) throw err("not-found", "Odkaz na akci neplatí.");
      const id = c.get("Akce");
      const ref = db.doc(`Akce/${id}`);
      const name = anonymous ? str(x.name, 40) : await nickOf(me);
      if (!name) throw err("invalid-argument", "Napiš jméno.");
      const answer = ["yes", "maybe", "no"].includes(x.answer) ? x.answer : "yes";
      await db.runTransaction(async (tx) => {
        const d = await tx.get(ref);
        if (!d.exists || d.get("Deleted")) throw err("not-found", "Akce už neexistuje.");
        const a = d.data();
        const members = a.Members || [];
        if (!members.includes(me) && members.length >= MAX_MEMBERS) throw err("resource-exhausted", "Akce je plná.");
        const upd = { Members: FieldValue.arrayUnion(me), [`Status.${me}`]: answer, [`Nicks.${me}`]: name };
        if (anonymous) upd[`Guests.${me}`] = true;
        else if ((a.Guests || {})[me]) upd[`Guests.${me}`] = FieldValue.delete();   // host z odkazu si založil účet
        tx.update(ref, upd);
      });
      return { id };
    }

    // ----- veřejný náhled akce podle kódu (stránka odkazu před „Jdu“) -----
    if (x.action === "peek") {
      const c = await db.doc(`AkceCodes/${str(x.code, 20)}`).get();
      if (!c.exists) throw err("not-found", "Odkaz na akci neplatí.");
      const d = await db.doc(`Akce/${c.get("Akce")}`).get();
      if (!d.exists || d.get("Deleted")) throw err("not-found", "Akce už neexistuje.");
      const a = d.data();
      const going = Object.entries(a.Status || {}).filter(([, s]) => s === "yes").map(([u]) => a.Nicks?.[u] || "?");
      return { id: d.id, name: a.Name, from: ms(a.From), to: ms(a.To), place: a.Place || "", host: a.Nicks?.[a.Host] || "?", going, member: (a.Members || []).includes(me) };
    }

    if (anonymous && !["beer", "respond", "log"].includes(x.action)) throw err("permission-denied", "Tohle jde jen s účtem v appce.");

    // ----- založit -----
    if (x.action === "create") {
      const name = str(x.name, 60);
      const from = num(x.from), to = num(x.to);
      if (!name || !from || !to || to <= from) throw err("invalid-argument", "Chybí název nebo čas.");
      if (to - from > 7 * 86400000) throw err("invalid-argument", "Akce může trvat nejvýš týden.");
      const kegs = (Array.isArray(x.kegs) ? x.kegs : []).slice(0, 20)
        .map((k) => ({ Name: str(k?.name, 40) || "Bečka", Liters: Math.min(200, Math.max(1, Number(k?.liters) || 30)), Beers: 0, TappedAt: null, EmptyAt: null }));
      const myNick = await nickOf(me);
      const ref = db.collection("Akce").doc();
      let code;
      for (let i = 0; i < 5; i++) { code = rnd(8); if (!(await db.doc(`AkceCodes/${code}`).get()).exists) break; }
      await db.doc(`AkceCodes/${code}`).set({ Akce: ref.id });
      await ref.set({
        Name: name, From: Timestamp.fromMillis(from), To: Timestamp.fromMillis(to),
        Lat: num(x.lat), Lon: num(x.lon), Place: str(x.place, 120), Host: me, Code: code,
        Theme: typeof x.theme === "object" && x.theme ? x.theme : {},
        Members: [me], Status: { [me]: "yes" }, Nicks: { [me]: myNick }, Guests: {}, Names: {},
        Kegs: kegs, Keg: -1, Rows: {}, History: [{ By: myNick, At: Timestamp.now(), What: "založil akci", Before: null }],
        Version: 1, CreatedAt: FieldValue.serverTimestamp(),
      });
      return { id: ref.id, code };
    }

    // ----- pozvat kámoše -----
    if (x.action === "invite") {
      const { ref, a } = await load(x.id, me, true);
      const friends = new Set(await friendsOf(me));
      const add = [...new Set((x.members || []).filter((u) => typeof u === "string" && !(a.Members || []).includes(u)))];
      if (add.some((u) => !friends.has(u))) throw err("permission-denied", "Zvát jde jen vlastní kámoše.");
      if ((a.Members || []).length + add.length > MAX_MEMBERS) throw err("resource-exhausted", "Tolik lidí se k Pepovi nevejde.");
      if (add.length === 0) return { ok: true };
      const upd = { Members: FieldValue.arrayUnion(...add) };
      for (const u of add) { upd[`Status.${u}`] = "wait"; upd[`Nicks.${u}`] = await nickOf(u); }
      await ref.update(upd);
      const myNick = a.Nicks?.[me] || (await nickOf(me));
      const p = prague(a.From.toDate());
      await notifyData(add, {
        type: "akceInvite", akce: ref.id,
        title: `${myNick} tě zve: ${a.Name}`,
        body: `${Number(p.iso.slice(8))}. ${Number(p.iso.slice(5, 7))}. od ${p.hm}${a.Place ? " · " + a.Place : ""} – jdeš? 🍻`,
        fromUid: me,
      });
      return { ok: true };
    }

    // ----- Jdu / Možná / Nejdu -----
    if (x.action === "respond") {
      const { ref, a } = await load(x.id, me);
      const answer = ["yes", "maybe", "no"].includes(x.answer) ? x.answer : "no";
      await ref.update({ [`Status.${me}`]: answer });
      if (answer === "yes" && a.Host !== me && !(a.Guests || {})[me]) {
        await notifyData([a.Host], { type: "akceInvite", akce: ref.id, title: `${a.Nicks?.[me] || "Někdo"} jde na ${a.Name} 🍻`, body: "" });
      }
      return { ok: true };
    }

    const sameTheme = (x, y) => {
      const keys = new Set([...Object.keys(x), ...Object.keys(y)].filter((k) => !k.endsWith("Img")));
      for (const k of keys) if (String(x[k] ?? "") !== String(y[k] ?? "")) return false;
      return true;
    };
    // ----- úprava (kdokoli, kdo jde; do historie kdo a co bylo předtím) -----
    if (x.action === "update") {
      const { ref, a } = await load(x.id, me, true);
      const p = x.patch || {};
      const myNick = a.Nicks?.[me] || "?";
      const upd = {};
      let hist = a.History || [];
      const change = (field, value, label, before) => {
        upd[field] = value;
        hist = history({ History: hist }, myNick, label, before);
      };
      if (p.name !== undefined && str(p.name, 60) && str(p.name, 60) !== a.Name) change("Name", str(p.name, 60), "přejmenoval akci", a.Name);
      if (p.from !== undefined && num(p.from) && num(p.from) !== ms(a.From)) change("From", ts(p.from), "změnil začátek", ms(a.From));
      if (p.to !== undefined && num(p.to) && num(p.to) !== ms(a.To)) change("To", ts(p.to), "změnil konec", ms(a.To));
      if (p.place !== undefined && str(p.place, 120) !== (a.Place || "")) change("Place", str(p.place, 120), "změnil místo", a.Place || "");
      if (num(p.lat) !== null && num(p.lon) !== null && (Math.abs(p.lat - (a.Lat ?? 999)) > 1e-6 || Math.abs(p.lon - (a.Lon ?? 999)) > 1e-6)) { upd.Lat = p.lat; upd.Lon = p.lon; hist = history({ History: hist }, myNick, "posunul špendlík", `${a.Lat},${a.Lon}`); }
      if (p.theme !== undefined && typeof p.theme === "object" && p.theme && !sameTheme(p.theme, a.Theme || {})) change("Theme", p.theme, "změnil výzdobu", null);
      if (Array.isArray(p.kegs)) {
        // naplánované bečky: už naražené / dopité zůstávají, ostatní se nahradí seznamem
        const done = (a.Kegs || []).filter((k) => k.TappedAt);
        const planned = p.kegs.slice(0, 20).map((k) => ({ Name: str(k?.name, 40) || "Bečka", Liters: Math.min(200, Math.max(1, Number(k?.liters) || 30)), Beers: 0, TappedAt: null, EmptyAt: null }));
        const sig = (ks) => ks.map((k) => `${k.Name}|${k.Liters}`).join(";");
        if (sig(planned) !== sig((a.Kegs || []).filter((k) => !k.TappedAt))) change("Kegs", [...done, ...planned], "upravil bečky", (a.Kegs || []).map((k) => `${k.Name} ${k.Liters} l`).join(", "));
      }
      const fromMs = upd.From ? ms(upd.From) : ms(a.From), toMs = upd.To ? ms(upd.To) : ms(a.To);
      if (toMs <= fromMs || toMs - fromMs > 7 * 86400000) throw err("invalid-argument", "Konec musí být po začátku a nejvýš za týden.");
      if (Object.keys(upd).length === 0) return { ok: true };
      upd.History = hist;
      upd.Version = (a.Version || 0) + 1;
      await ref.update(upd);
      return { ok: true };
    }

    // ----- vrátit změnu z historie -----
    if (x.action === "revert") {
      const { ref, a } = await load(x.id, me, true);
      const h = (a.History || [])[Number(x.index)];
      if (!h || h.Before === null || h.Before === undefined) throw err("failed-precondition", "Tohle vrátit nejde.");
      const map = { "přejmenoval akci": ["Name", h.Before], "změnil začátek": ["From", ts(h.Before)], "změnil konec": ["To", ts(h.Before)], "změnil místo": ["Place", h.Before] };
      const m = map[h.What];
      if (!m) throw err("failed-precondition", "Tohle vrátit nejde.");
      await ref.update({ [m[0]]: m[1], History: history(a, a.Nicks?.[me] || "?", `vrátil: ${h.What}`, null), Version: (a.Version || 0) + 1 });
      return { ok: true };
    }

    // ----- smazat (jen hostitel) -----
    if (x.action === "delete") {
      const { ref, a } = await load(x.id, me);
      if (a.Host !== me) throw err("permission-denied", "Akci může smazat jen hostitel.");
      await ref.update({ Deleted: true, Version: (a.Version || 0) + 1 });
      if (a.Code) await db.doc(`AkceCodes/${a.Code}`).delete().catch(() => {});
      return { ok: true };
    }

    // ----- člověk jen se jménem (píšou mu ostatní) -----
    if (x.action === "addName") {
      const { ref, a } = await load(x.id, me, true);
      const name = str(x.name, 40);
      if (!name) throw err("invalid-argument", "Napiš jméno.");
      const key = `n_${rnd(8)}`;
      await ref.update({ [`Nicks.${key}`]: name, [`Names.${key}`]: true, [`Rows.${key}`]: { N: 0 }, Version: (a.Version || 0) + 1 });
      return { key };
    }

    // ----- pivo na akční lístek (komukoli; za appkáře i do jeho lístku) -----
    if (x.action === "beer") {
      const delta = x.delta === -1 ? -1 : 1;
      const ref = db.doc(`Akce/${x.id}`);
      let next, akceId = x.id;
      await db.runTransaction(async (tx) => {
        const d = await tx.get(ref);
        if (!d.exists || d.get("Deleted")) throw err("not-found", "Akce už neexistuje.");
        const a = d.data();
        if (!(a.Members || []).includes(me) || !["yes", "maybe"].includes(a.Status?.[me])) throw err("permission-denied", "Nejdřív dej „Jdu“.");
        const who = typeof x.who === "string" ? x.who : me;
        const isMember = (a.Members || []).includes(who);
        if (!isMember && !(a.Names || {})[who]) throw err("not-found", "Tenhle člověk na akci není.");
        if (now < ms(a.From) - 3600000 || now > ms(a.To) + 3600000) throw err("failed-precondition", "Akce teď neběží.");
        const byNick = a.Nicks?.[me] || "?";
        next = applyBeer(a, who, delta, byNick, now);
        if (isMember && !(a.Guests || {})[who] && !(a.Names || {})[who]) drinkWrites(tx, who, delta, me, byNick, akceId, now);
        if (isMember && a.Status?.[who] === "wait") next.Status = { ...next.Status, [who]: "yes" };
        tx.update(ref, { Rows: next.Rows, Kegs: next.Kegs, Version: next.Version, ...(next.Status ? { Status: next.Status } : {}) });
        tx.create(ref.collection("Log").doc(), { Who: who, Delta: delta, At: Timestamp.fromMillis(now), By: me, ByNick: byNick, Src: "akce" });
      });
      await pushState(akceId, next, me).catch((e) => logger.warn(`push akce ${akceId}`, e));
      return { state: state(akceId, next) };
    }

    // ----- přeraženo: dopitá bečka → narazit vybranou / jinou -----
    if (x.action === "tap") {
      const ref = db.doc(`Akce/${x.id}`);
      let next;
      await db.runTransaction(async (tx) => {
        const d = await tx.get(ref);
        if (!d.exists || d.get("Deleted")) throw err("not-found", "Akce už neexistuje.");
        const a = d.data();
        if (!(a.Members || []).includes(me) || !["yes", "maybe"].includes(a.Status?.[me])) throw err("permission-denied", "Nejdřív dej „Jdu“.");
        const kegs = (a.Kegs || []).map((k) => ({ ...k }));
        const byNick = a.Nicks?.[me] || "?";
        let hist = a.History || [];
        if (a.Keg >= 0 && kegs[a.Keg]) {
          kegs[a.Keg].EmptyAt = Timestamp.fromMillis(now);
          const p = prague(new Date(now));
          hist = history({ History: hist }, byNick, `bečka ${kegs[a.Keg].Name} padla v ${p.hm} po ${kegs[a.Keg].Beers || 0} pivech`, null);
        }
        let idx = -1;
        if (x.custom) {
          kegs.push({ Name: str(x.custom.name, 40) || "Bečka", Liters: Math.min(200, Math.max(1, Number(x.custom.liters) || 30)), Beers: 0, TappedAt: null, EmptyAt: null });
          idx = kegs.length - 1;
        } else if (Number.isInteger(x.keg) && kegs[x.keg] && !kegs[x.keg].TappedAt) idx = x.keg;
        else if (x.keg !== null && x.keg !== undefined && x.keg !== -1) throw err("invalid-argument", "Tahle bečka už byla naražená.");
        if (idx >= 0) {
          kegs[idx].TappedAt = Timestamp.fromMillis(now);
          kegs[idx].Beers = 0;
          hist = history({ History: hist }, byNick, `narazil ${kegs[idx].Name} ${kegs[idx].Liters} l`, null);
        }
        next = { ...a, Kegs: kegs, Keg: idx, History: hist, Version: (a.Version || 0) + 1 };
        tx.update(ref, { Kegs: kegs, Keg: idx, History: hist, Version: next.Version });
      });
      await pushState(x.id, next, me).catch((e) => logger.warn(`push akce ${x.id}`, e));
      return { state: state(x.id, next) };
    }

    // ----- detail člověka: jeho zápisy na akci -----
    if (x.action === "log") {
      await load(x.id, me);
      const who = typeof x.who === "string" ? x.who : me;
      const snap = await db.collection(`Akce/${x.id}/Log`).where("Who", "==", who).get();
      const items = snap.docs.map((d) => ({ at: ms(d.get("At")), delta: d.get("Delta"), by: d.get("ByNick") || "?", src: d.get("Src") }))
        .sort((p, q) => q.at - p.at).slice(0, 200);
      return { items };
    }

    // ----- statistiky akce (průběžně) / výsledky -----
    if (x.action === "stats") {
      const { a } = await load(x.id, me);
      return await stats(x.id, a);
    }

    // ----- vlastní obrázek výzdoby (base64 zmenšený v telefonu, max ~180 kB) -----
    if (x.action === "media") {
      const { ref, a } = await load(x.id, me, true);
      const slot = ["top", "corner", "fall", "coaster"].includes(x.slot) ? x.slot : null;
      if (!slot) throw err("invalid-argument", "Neznámé místo výzdoby.");
      const data = typeof x.data === "string" ? x.data : "";
      if (data.length > 250000) throw err("invalid-argument", "Obrázek je moc velký.");
      const mediaRef = ref.collection("Media").doc(slot);
      if (!data) await mediaRef.delete().catch(() => {});
      else await mediaRef.set({ Data: data, Mime: str(x.mime, 30) || "image/png", By: me, At: FieldValue.serverTimestamp() });
      await ref.update({ [`Theme.${slot}Img`]: data ? Date.now() : FieldValue.delete(), [`Theme.${slot}`]: data ? "img" : "preset", Version: (a.Version || 0) + 1,
        History: history(a, a.Nicks?.[me] || "?", data ? "nahrál obrázek výzdoby" : "smazal obrázek výzdoby", null) });
      return { ok: true };
    }

    throw err("invalid-argument", "Neznámá akce.");
  });

  // ---------------- pivo z běžného lístku v místě akce ----------------

  out.akceDrink = onDocumentCreated("Drinks/{id}", async (event) => {
    const d = event.data?.data();
    if (!d || typeof d.Akce !== "string" || d.AkceBy || !d.UserId) return;   // AkceBy = zapsáno z akčního lístku, už sečtené
    const delta = Number(d.Pivo) || 0;
    if (delta === 0 && !(Number(d.Vino) || Number(d.Panaky) || Number(d.Michacky))) return;
    const ref = db.doc(`Akce/${d.Akce}`);
    let next = null;
    await db.runTransaction(async (tx) => {
      const doc = await tx.get(ref);
      if (!doc.exists || doc.get("Deleted")) return;
      const a = doc.data();
      if (!(a.Members || []).includes(d.UserId)) return;
      const at = d.DrinkAt?.toMillis?.() ?? Date.now();
      if (at < ms(a.From) - 3600000 || at > ms(a.To) + 3600000) return;
      if (delta !== 0) {
        if (delta < 0 && ((a.Rows || {})[d.UserId]?.N || 0) <= 0) return;
        next = applyBeer(a, d.UserId, delta, a.Nicks?.[d.UserId] || "?", at);
        tx.update(ref, { Rows: next.Rows, Kegs: next.Kegs, Version: next.Version });
        tx.create(ref.collection("Log").doc(), { Who: d.UserId, Delta: delta, At: Timestamp.fromMillis(at), By: d.UserId, ByNick: a.Nicks?.[d.UserId] || "?", Src: "lístek" });
      }
    });
    if (next) await pushState(d.Akce, next, d.UserId).catch((e) => logger.warn(`push akce ${d.Akce}`, e));
  });

  // ---------------- statistiky a výsledky ----------------

  async function stats(id, a) {
    const rows = a.Rows || {};
    const log = (await db.collection(`Akce/${id}/Log`).get()).docs.map((d) => d.data());
    const drinks = (await db.collection("Drinks").where("Akce", "==", id).get()).docs.map((d) => d.data());
    const people = {};
    const P = (k) => (people[k] ||= { key: k, name: a.Nicks?.[k] || "?", pivo: 0, vino: 0, panaky: 0, michacky: 0, first: 0, last: 0, beers: [] });
    for (const [k, r] of Object.entries(rows)) P(k).pivo = r.N || 0;
    for (const l of log) {
      const p = P(l.Who), t = ms(l.At);
      if (l.Delta > 0) { p.beers.push(t); p.first = p.first ? Math.min(p.first, t) : t; p.last = Math.max(p.last, t); }
    }
    for (const dr of drinks) {
      if (dr.AkceBy) continue;   // piva z akčního lístku jsou už v řádcích
      const p = P(dr.UserId);
      p.vino += Number(dr.Vino) || 0; p.panaky += Number(dr.Panaky) || 0; p.michacky += Number(dr.Michacky) || 0;
      const t = ms(dr.DrinkAt);
      if ((Number(dr.Vino) || 0) + (Number(dr.Panaky) || 0) + (Number(dr.Michacky) || 0) > 0) {
        p.first = p.first ? Math.min(p.first, t) : t; p.last = Math.max(p.last, t);
      }
    }
    const list = Object.values(people).map((p) => {
      p.beers.sort((x, y) => x - y);
      const tempo = p.beers.length >= 3 ? Math.round((p.beers[p.beers.length - 1] - p.beers[0]) / (p.beers.length - 1) / 60000) : null;
      return { key: p.key, name: p.name, pivo: p.pivo, vino: p.vino, panaky: p.panaky, michacky: p.michacky,
        total: p.pivo + p.vino + p.panaky + p.michacky, first: p.first, last: p.last, tempo };
    }).filter((p) => p.total > 0).sort((x, y) => y.pivo - x.pivo || y.total - x.total);
    // po dnech (hospodský den)
    const days = {};
    for (const l of log) { const day = prague(new Date(ms(l.At))).pub; days[day] = (days[day] || 0) + (l.Delta || 0); }
    const pivo = list.reduce((s, p) => s + p.pivo, 0);
    const best = (f, cmp) => list.filter(f).sort(cmp)[0]?.name || null;
    const titles = {
      kralPiva: best((p) => p.pivo > 0, (x, y) => y.pivo - x.pivo),
      kralVecera: best((p) => p.total > 0, (x, y) => y.total - x.total),
      rychlik: best((p) => p.tempo !== null, (x, y) => x.tempo - y.tempo),
      vytrvalec: best((p) => p.last > 0, (x, y) => y.last - x.last),
      padlPrvni: list.filter((p) => p.last > 0).length >= 3 ? best((p) => p.last > 0, (x, y) => x.last - y.last) : null,
    };
    const kegs = (a.Kegs || []).filter((k) => k.TappedAt).map((k) => ({ name: k.Name, liters: k.Liters, beers: k.Beers || 0, tapped: ms(k.TappedAt), empty: ms(k.EmptyAt) }));
    return { pivo, liters: Math.round(pivo * BEER_L * 10) / 10, people: list, days, titles, kegs };
  }

  // každých 30 min: skončené akce → výsledky (zapsat do akce + push účastníkům)
  out.akceResults = onSchedule({ schedule: "every 30 minutes", region: "europe-west3", timeZone: "Europe/Prague" }, async () => {
    const snap = await db.collection("Akce").where("To", "<=", Timestamp.now())
      .where("To", ">=", Timestamp.fromMillis(Date.now() - 3 * 86400000)).get();
    for (const doc of snap.docs) {
      const a = doc.data();
      if (a.Results || a.Deleted) continue;
      const res = await stats(doc.id, a);
      await doc.ref.update({ Results: { ...res, At: Timestamp.now() }, Version: (a.Version || 0) + 1 });
      if (res.pivo === 0 && res.people.length === 0) continue;
      const going = (a.Members || []).filter((u) => ["yes", "maybe"].includes(a.Status?.[u]) && !(a.Guests || {})[u]);
      await notifyData(going, {
        type: "akceResults", akce: doc.id,
        title: `${a.Name}: výsledky 🏆`,
        body: `${piv(res.pivo)} (${String(res.liters).replace(".", ",")} l)${res.titles.kralPiva ? ` · Král piva: ${res.titles.kralPiva}` : ""}`,
      });
      logger.info(`Akce ${doc.id}: výsledky, ${res.pivo} piv`);
    }
  });

  // pro testy
  out._test = { state, prague, versionOk };
  return out;
};
