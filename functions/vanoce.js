/**
 * Dezoláti – eventy (Vánoce 2026): vánoční trhy a odznaky.
 *
 *  Events/aktualni         – JEDEN dokument pro appku i web (čte se 1× denně): event, popisy odznaků, zóny trhů.
 *                            Píše jen server (sestaví ho rebuild() z balíčku + oprav + nahlášených trhů).
 *  EventsAdmin/zdroj       – podklady pro Events/aktualni: opravy termínů (Overrides), nahlášené trhy (Reported).
 *                            Z appky nečitelné.
 *  TrhReports/{id}         – „Tady jsou trhy!“ (appka zakládá, čte a maže jen správce). Status píše server:
 *                            pending / known (už v seznamu) / approved / rejected.
 *  Achievements/{uid}      – získané odznaky + postup („2/4 neděle“). Čte každý přihlášený (kámoši ve statistikách).
 *  AchievementState/{uid}  – podklady k vyhodnocení (dny, trhy, zaokrouhlené polohy). Jen server.
 *
 *  Drinks/{id} během eventu (od appky 3.13): TrhId (trh poznaný v telefonu) + EvLat/EvLon (poloha zaokrouhlená
 *  na 0,001° ≈ 100 m) – ze zaokrouhlené polohy se zpětně rozdají odznaky, když se trh schválí až později.
 *  Starší verze appky tyhle údaje neposílají → dostanou odznaky za data, ne za trhy.
 *
 *  Nový odznak = řádek v BADGES (popis do appky) + podmínka ve vyhodnocení níž.
 */
const { onDocumentCreated, onDocumentUpdated } = require("firebase-functions/v2/firestore");
const { onCall, HttpsError } = require("firebase-functions/v2/https");
const { onSchedule } = require("firebase-functions/v2/scheduler");
const { logger } = require("firebase-functions/v2");
const { Timestamp, FieldValue } = require("firebase-admin/firestore");
const crypto = require("crypto");
const BUNDLE = require("./events/trhy-2026.json");

module.exports = function vanoce({ db, notifyData, friendsOf, ADMIN_UID }) {
  const out = {};

  // ---------------- event a odznaky ----------------

  const EVENT = {
    Id: "vanoce2026",
    Name: "Vánoce 2026",
    Theme: "vanoce",
    From: "2026-11-27",              // start v pátek 27. 11. (hospodský den od 6:00): motiv i odznaky až od něj (dřív jen správce v náhledu)
    To: "2027-01-06",
    Advent: ["2026-11-29", "2026-12-06", "2026-12-13", "2026-12-20"],
    // motiv podle času (Praha): vánoční, na Silvestra sekt + ohňostroj + konfety, po Novém roce jen zima
    Themes: [
      { Theme: "vanoce", From: "2026-11-27T06:00", To: "2026-12-31T06:00" },
      { Theme: "silvestr", From: "2026-12-31T06:00", To: "2027-01-01T12:00" },
      { Theme: "zima", From: "2027-01-01T12:00", To: "2027-01-07T06:00" },
    ],
  };

  const ADVENT = "Advent", VANOCE = "Vánoce", SILVESTR = "Silvestr a Nový rok", META = "Meta";
  // Icon: candle tree pin glass people gift heart bell bottle mug letter calendar star clock moon crown medal halo
  const BADGES = [
    { Id: "prvniSvicka", Hint: "29. 11.", Name: "První svíčka", Desc: "Zápis 1. adventní neděli (29. 11.)", Icon: "candle", Color: "#B3262E", Group: ADVENT },
    { Id: "adventniStamgast", Hint: "4 adventní neděle", Name: "Adventní štamgast", Desc: "Zápis všechny 4 adventní neděle", Icon: "candle", Color: "#B3262E", Group: ADVENT, Need: 4, Unit: "neděle" },
    { Id: "adventniKalendar", Hint: "24 okýnek", Name: "Adventní kalendář", Desc: "Otevřít všech 24 okýnek adventního kalendáře (zápis každý den 1.–24. 12., zmeškaný den zachrání druhý den dva panáky)", Icon: "calendar", Color: "#B3262E", Group: ADVENT, Need: 24, Unit: "okýnek" },
    { Id: "mikulasska", Hint: "5. 12. večer", Name: "Mikulášská", Desc: "Zápis 5. 12. večer (od 17:00)", Icon: "bell", Color: "#C9892B", Group: ADVENT },
    { Id: "lucie", Hint: "13. 12. po 22:00", Name: "Lucie", Desc: "Zápis 13. 12. po 22:00 – Lucie noci upije", Icon: "moon", Color: "#3C6E9E", Group: ADVENT },
    { Id: "trhovec", Hint: "na trzích", Name: "Trhovec", Desc: "Zápis na vánočních trzích", Icon: "tree", Color: "#2E6B3A", Group: ADVENT },
    { Id: "trhovaTurne", Hint: "3 dny na trzích", Name: "Trhová turné", Desc: "Trhy ve 3 různých dnech", Icon: "pin", Color: "#2E6B3A", Group: ADVENT, Need: 3, Unit: "dny" },
    { Id: "svarak", Hint: "víno na trzích", Name: "Svařák", Desc: "Víno na trzích – svařák v kelímku se počítá", Icon: "glass", Color: "#8E1B2A", Group: ADVENT },
    { Id: "vecirek", Hint: "4 kámoši v hospodě", Name: "Večírek", Desc: "Ve stejné hospodě s aspoň 4 kámoši (hlásíte hospodu)", Icon: "people", Color: "#3C6E9E", Group: ADVENT, Need: 4, Unit: "kámoši" },
    { Id: "kolednik", Hint: "3 hospody za večer", Name: "Koledník", Desc: "Tři různé hospody za jeden večer", Icon: "lantern", Color: "#C9892B", Group: ADVENT, Need: 3, Unit: "hospody" },
    { Id: "rozdavac", Hint: "10 kámošům", Name: "Rozdávač", Desc: "Připít 10 různým kámošům", Icon: "gift", Color: "#B3262E", Group: ADVENT, Need: 10, Unit: "kámošů" },
    { Id: "oblibenec", Hint: "od 5 kámošů", Name: "Oblíbenec", Desc: "Přípitek od 5 různých kámošů", Icon: "heart", Color: "#B3262E", Group: ADVENT, Need: 5, Unit: "kámošů" },

    { Id: "kaprVHospode", Hint: "23. 12. večer v hospodě", Name: "Kapr v hospodě", Desc: "Hospoda 23. 12. večer (od 17:00) – kapr ve vaně počká", Icon: "fish", Color: "#3C6E9E", Group: VANOCE },
    { Id: "naJednoPredVecery", Hint: "24. 12. do 15:00", Name: "Na jedno před večeří", Desc: "Hospoda 24. 12. do 15:00", Icon: "mug", Color: "#6B3E1E", Group: VANOCE },
    { Id: "stedrovecerniPripitek", Hint: "přípitek 24. 12.", Name: "Štědrovečerní přípitek", Desc: "Připít kámošovi 24. 12.", Icon: "star", Color: "#C9892B", Group: VANOCE },
    { Id: "slivovicePodStromecek", Hint: "24.–26. 12.", Name: "Slivovice pod stromeček", Desc: "Slivovice 24.–26. 12.", Icon: "bottle", Color: "#6B3E1E", Group: VANOCE },
    { Id: "stepanska", Hint: "hospoda 26. 12.", Name: "Štěpánská", Desc: "Hospoda 26. 12.", Icon: "mug", Color: "#2E6B3A", Group: VANOCE },
    { Id: "vanocniPozvanka", Hint: "pozvánka pro 4", Name: "Vánoční pozvánka", Desc: "Pozvánka, na kterou řeknou „Jdu“ aspoň 4 lidi", Icon: "letter", Color: "#B3262E", Group: VANOCE, Need: 4, Unit: "lidí" },
    { Id: "meziSvatky", Hint: "3 dny 27.–30. 12.", Name: "Mezi svátky", Desc: "Zápis 3 dny z 27.–30. 12.", Icon: "calendar", Color: "#3C6E9E", Group: VANOCE, Need: 3, Unit: "dny" },

    { Id: "pulnoc", Hint: "31. 12. o půlnoci", Name: "Půlnoc", Desc: "Zápis 31. 12. mezi 23:30 a 0:30", Icon: "clock", Color: "#C9892B", Group: SILVESTR },
    { Id: "bubliny", Hint: "víno o půlnoci", Name: "Bubliny", Desc: "Víno 31. 12. mezi 23:00 a 1:00", Icon: "glass", Color: "#C9892B", Group: SILVESTR },
    { Id: "vyprostovak", Hint: "1. 1. dopoledne", Name: "Vyprošťovák", Desc: "Zápis 1. 1. mezi 6:00 a polednem", Icon: "mug", Color: "#3C6E9E", Group: SILVESTR },
    { Id: "suchyStart", Hint: "2.–5. 1. nic", Name: "Suchý start", Desc: "2.–5. 1. bez jediného zápisu (bonus, do Legendy se nepočítá)", Icon: "moon", Color: "#3C6E9E", Group: SILVESTR, Bonus: true },
    { Id: "triKralove", Hint: "6. 1. tři druhy", Name: "Tři králové", Desc: "Tři různé druhy pití 6. 1.", Icon: "crown", Color: "#C9892B", Group: SILVESTR, Need: 3, Unit: "druhy" },

    { Id: "sberatel", Hint: "10 odznaků", Name: "Sběratel", Desc: "10 vánočních odznaků", Icon: "medal", Color: "#6B3E1E", Group: META, Meta: true, Need: 10, Unit: "odznaků" },
    { Id: "vanocniLegenda", Hint: "všechny odznaky", Name: "Vánoční legenda", Desc: "Všechny odznaky kromě Suchého startu", Icon: "star", Color: "#B3262E", Group: META, Meta: true },
    { Id: "svatozar", Hint: "legenda + suchý start", Name: "Svatozář", Desc: "Vánoční legenda + Suchý start (pro svatý a játra)", Icon: "halo", Color: "#E3B23C", Group: META, Meta: true },
  ];
  const BY_ID = Object.fromEntries(BADGES.map((b) => [b.Id, b]));
  const NORMAL = BADGES.filter((b) => !b.Bonus && !b.Meta).map((b) => b.Id);

  // ---------------- čas (Praha, hospodský den do 6:00) ----------------

  const PF = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Europe/Prague", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23",
  });
  const isoAdd = (iso, n) => new Date(Date.parse(iso + "T00:00:00Z") + n * 86400000).toISOString().slice(0, 10);
  /** { iso: kalendářní den, min: minuty od půlnoci, pubDay: hospodský den } */
  function prague(date) {
    const p = Object.fromEntries(PF.formatToParts(date).map((x) => [x.type, x.value]));
    const iso = `${p.year}-${p.month}-${p.day}`;
    const h = Number(p.hour), m = Number(p.minute);
    return { iso, min: h * 60 + m, pubDay: h < 6 ? isoAdd(iso, -1) : iso };
  }
  const inEvent = (day) => day >= EVENT.From && day <= EVENT.To;
  const between = (day, a, b) => day >= a && day <= b;
  const fmtDay = (iso) => `${Number(iso.slice(8, 10))}. ${Number(iso.slice(5, 7))}.`;

  // ---------------- geometrie zón (metry, rovinná aproximace – na pár set metrů stačí) ----------------

  const M_LAT = 111320;
  const mLon = (lat) => 111320 * Math.cos((lat * Math.PI) / 180);
  function distM(a, b) {
    const dy = (a[0] - b[0]) * M_LAT, dx = (a[1] - b[1]) * mLon((a[0] + b[0]) / 2);
    return Math.hypot(dx, dy);
  }
  function segDistM(p, a, b) {
    const k = mLon(p[0]);
    const ax = (a[1] - p[1]) * k, ay = (a[0] - p[0]) * M_LAT, bx = (b[1] - p[1]) * k, by = (b[0] - p[0]) * M_LAT;
    const dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
    const t = len === 0 ? 0 : Math.max(0, Math.min(1, -(ax * dx + ay * dy) / len));
    return Math.hypot(ax + t * dx, ay + t * dy);
  }
  function inPoly(p, ring) {
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
      const [yi, xi] = ring[i], [yj, xj] = ring[j];
      if ((yi > p[0]) !== (yj > p[0]) && p[1] < ((xj - xi) * (p[0] - yi)) / (yj - yi) + xi) inside = !inside;
    }
    return inside;
  }
  const edgeDist = (p, pts, closed) => {
    let best = Infinity;
    for (let i = 0; i + 1 < pts.length; i++) best = Math.min(best, segDistM(p, pts[i], pts[i + 1]));
    if (closed && pts.length > 2) best = Math.min(best, segDistM(p, pts[pts.length - 1], pts[0]));
    if (pts.length === 1) best = distM(p, pts[0]);
    return best;
  };
  /** Je bod v zóně trhu (+ tolerance v metrech)? Zóna ve formátu Events/aktualni (P/L jako texty, C/R kruh). */
  function inZone(z, lat, lon, tol) {
    const p = [lat, lon];
    for (const s of z.P || []) { const r = decode(s); if (inPoly(p, r) || edgeDist(p, r, true) <= tol) return true; }
    for (const s of z.L || []) { if (edgeDist(p, decode(s), false) <= (z.W || 20) + tol) return true; }
    if (z.C) { const c = decode(z.C)[0]; if (distM(p, c) <= (z.R || 80) + tol) return true; }
    return false;
  }
  const active = (z, day) => day >= z.From && day <= z.To;

  // Firestore neumí pole v poli → body jako text „lat,lon lat,lon …“
  const encode = (pts) => pts.map(([a, b]) => `${a.toFixed(6)},${b.toFixed(6)}`).join(" ");
  const decode = (s) => s.split(" ").map((x) => x.split(",").map(Number));

  // ---------------- Events/aktualni (sestavení) ----------------

  // otisk balíčku trhů + eventu + odznaků: změna čehokoli → eventsSync přestaví Events/aktualni
  const BUNDLE_HASH = crypto.createHash("sha1").update(JSON.stringify([BUNDLE, EVENT, BADGES])).digest("hex").slice(0, 12);
  const STATUS = { potvrzeno: "potvrzeno", odhad: "odhad" };

  function fromBundle(t) {
    return {
      Id: t.id, Name: t.name, From: t.from, To: t.to, Status: STATUS[t.status] || "odhad", Src: t.source || "",
      P: (t.polys || []).map(encode), L: (t.lines || []).map(encode), W: t.w || 0,
    };
  }

  /** Sestaví Events/aktualni: balíček + opravy (termíny, název, stav) + nahlášené trhy. */
  async function rebuild() {
    const srcRef = db.doc("EventsAdmin/zdroj");
    const src = (await srcRef.get()).data() || {};
    const overrides = src.Overrides || {};
    const trhy = [...BUNDLE.trhy.map(fromBundle), ...(src.Reported || [])]
      .filter((z) => !(overrides[z.Id] && overrides[z.Id].Removed))
      .map((z) => ({ ...z, ...(overrides[z.Id] || {}) }));
    const doc = {
      Id: EVENT.Id, Name: EVENT.Name, Theme: EVENT.Theme, Themes: EVENT.Themes, From: EVENT.From, To: EVENT.To, Advent: EVENT.Advent,
      Badges: BADGES, Trhy: trhy, Bundle: BUNDLE_HASH, UpdatedAt: FieldValue.serverTimestamp(),
    };
    await db.doc("Events/aktualni").set(doc);
    zonesCache = { at: Date.now(), trhy };
    logger.info(`Events/aktualni: ${trhy.length} trhů, ${BADGES.length} odznaků`);
    return trhy;
  }

  let zonesCache = null;   // zóny v paměti instance (10 min), ať se neplatí čtení za každý zápis
  async function zones() {
    if (zonesCache && Date.now() - zonesCache.at < 10 * 60 * 1000) return zonesCache.trhy;
    const d = await db.doc("Events/aktualni").get();
    const trhy = d.exists ? d.get("Trhy") || [] : await rebuild();
    zonesCache = { at: Date.now(), trhy };
    return trhy;
  }

  // každý den ráno (a po nasazení stačí v Cloud Scheduleru „Force run“): když se změnil balíček, přestavět
  out.eventsSync = onSchedule({ schedule: "15 4 * * *", region: "europe-west3", timeZone: "Europe/Prague" }, async () => {
    const d = await db.doc("Events/aktualni").get();
    if (d.exists && d.get("Bundle") === BUNDLE_HASH) return;
    await rebuild();
  });

  // ---------------- odznaky: stav a udělení ----------------

  const addTo = (arr, v) => { if (!arr.includes(v)) arr.push(v); return arr; };

  /**
   * Upraví stav uživatele v transakci. fn(S, award, progress) – award(id, info) udělí odznak (jednou),
   * progress(id, n) zapíše postup (bere maximum). Nové odznaky rovnou oznámí.
   */
  async function update(uid, fn) {
    const stRef = db.doc(`AchievementState/${uid}`), acRef = db.doc(`Achievements/${uid}`);
    const fresh = await db.runTransaction(async (tx) => {
      const [st, ac] = await Promise.all([tx.get(stRef), tx.get(acRef)]);
      const S = { Days: [], Trh: [], Locs: [], CheerTo: [], CheerFrom: [], K6: [], Pubs: {}, Advent: {}, Pk: {}, ...(st.data() || {}) };
      const A = ac.data() || {};
      const got = { ...(A.Got || {}) };
      const prog = { ...(A.Progress || {}) };
      const now = Timestamp.now();
      const added = [];
      const award = (id, info) => { if (!got[id] && BY_ID[id]) { got[id] = { At: now, Info: info || "" }; added.push(id); } };
      const progress = (id, n) => { if (!(prog[id] >= n)) prog[id] = n; };

      fn(S, award, progress);

      // meta odznaky
      const count = Object.keys(got).filter((id) => !BY_ID[id]?.Meta).length;
      progress("sberatel", count);
      if (count >= 10) award("sberatel", "");
      if (NORMAL.every((id) => got[id])) award("vanocniLegenda", "");
      if (got.vanocniLegenda && got.suchyStart) award("svatozar", "");

      tx.set(stRef, { ...S, UpdatedAt: now });
      const advent = S.Advent || {};
      if (added.length || JSON.stringify(prog) !== JSON.stringify(A.Progress || {}) || JSON.stringify(advent) !== JSON.stringify(A.Advent || {}) || !ac.exists) {
        tx.set(acRef, { Event: EVENT.Id, Got: got, Progress: prog, Advent: advent, Count: Object.keys(got).length, UpdatedAt: now });
      }
      return added;
    });

    if (fresh.length) {
      const names = fresh.map((id) => BY_ID[id].Name);
      await notifyData([uid], {
        type: "badge",
        event: EVENT.Id,
        badge: fresh[0],
        title: fresh.length === 1 ? `Nový odznak: ${names[0]}!` : `Nové odznaky: ${names.join(", ")}!`,
        body: fresh.length === 1 ? BY_ID[fresh[0]].Desc : "Ukážou se ve Statistikách.",
      });
      logger.info(`Odznaky ${uid}: ${fresh.join(", ")}`);
    }
    return fresh;
  }

  /** Den na trzích → Trhovec, Trhová turné, Svařák. */
  function trhDay(S, award, progress, day, trhId, wine) {
    addTo(S.Trh, `${day}|${trhId}`);
    award("trhovec", fmtDay(day));
    const days = new Set(S.Trh.map((x) => x.slice(0, 10)));
    progress("trhovaTurne", days.size);
    if (days.size >= 3) award("trhovaTurne", fmtDay(day));
    if (wine) award("svarak", fmtDay(day));
  }

  // ---------------- zápis pití ----------------

  const n0 = (x) => Math.max(0, Number(x) || 0);
  const SESSION_MS = 3 * 60 * 60 * 1000;

  out.badgesDrink = onDocumentCreated("Drinks/{id}", async (event) => {
    const d = event.data?.data();
    if (!d || d.Correction || !d.UserId || typeof d.Day !== "string" || !inEvent(d.Day)) return;
    const kinds = [["pivo", d.Pivo], ["vino", d.Vino], ["panak", d.Panaky], ["michacka", d.Michacky]]
      .filter(([, v]) => n0(v) > 0).map(([k]) => k);
    if (kinds.length === 0) return;

    const day = d.Day;
    const at = d.DrinkAt?.toDate?.() || null;
    const t = at ? prague(at) : null;
    const timed = t && t.pubDay === day;   // zapsáno „teď“ (dodatečný zápis za jiný den nemá čas)
    const wine = n0(d.Vino) > 0;
    const pub = typeof d.PubId === "string" && d.PubId.length > 0;

    // trh: TrhId poznal telefon; ověřit, že trh existuje a ten den běží
    let trh = null;
    if (typeof d.TrhId === "string") {
      const z = (await zones()).find((x) => x.Id === d.TrhId);
      if (z && active(z, day)) trh = z;
    }

    // Večírek: kámoši, kteří teď hlásí stejnou hospodu
    let partyFriends = 0;
    if (pub && timed && d.SharePub === true) {
      const [friends, here] = await Promise.all([
        friendsOf(d.UserId),
        db.collection("Users").where("CurrentPubId", "==", d.PubId).get(),
      ]);
      const fs = new Set(friends);
      partyFriends = here.docs.filter((u) => fs.has(u.id) && (u.get("CurrentPubAt")?.toMillis?.() ?? 0) >= at.getTime() - SESSION_MS).length;
    }

    await update(d.UserId, (S, award, progress) => {
      addTo(S.Days, day);

      // advent
      if (day === EVENT.Advent[0]) award("prvniSvicka", fmtDay(day));
      const sundays = EVENT.Advent.filter((x) => S.Days.includes(x)).length;
      progress("adventniStamgast", sundays);
      if (sundays === 4) award("adventniStamgast", fmtDay(day));
      if (timed && day === "2026-12-05" && (t.min >= 17 * 60 || t.iso !== day)) award("mikulasska", fmtDay(day));

      // adventní kalendář: okýnko = zápis v ten den (dodatečný zápis za jiný den se nepočítá);
      // zmeškané okýnko zachrání hned další den dva trestné panáky
      if (timed && between(day, "2026-12-01", "2026-12-25")) {
        S.Advent = S.Advent || {};
        S.Pk = S.Pk || {};
        const num = Number(day.slice(8, 10));
        if (num <= 24 && !S.Advent[num]) S.Advent[num] = "d";
        if (n0(d.Panaky) > 0) {
          S.Pk = { [day]: (S.Pk[day] || 0) + n0(d.Panaky) };   // stačí dnešek
          if (S.Pk[day] >= 2 && num - 1 >= 1 && !S.Advent[num - 1]) S.Advent[num - 1] = "p";
        }
        const opened = Object.keys(S.Advent).length;
        progress("adventniKalendar", opened);
        if (opened >= 24) award("adventniKalendar", "");
      }
      if (timed && day === "2026-12-13" && (t.min >= 22 * 60 || t.iso !== day)) award("lucie", fmtDay(day));

      // Koledník: různé hospody za jeden hospodský den
      if (pub) {
        const list = S.Pubs[day] || [];
        if (!list.includes(d.PubId)) list.push(d.PubId);
        S.Pubs = { [day]: list, ...Object.fromEntries(Object.entries(S.Pubs).filter(([k]) => k !== day).slice(-20)) };
        progress("kolednik", list.length);
        if (list.length >= 3) award("kolednik", fmtDay(day));
      }

      // trhy (zaokrouhlená poloha bez trhu se schová na později – zpětné rozdání po schválení)
      if (trh) trhDay(S, award, progress, day, trh.Id, wine);
      else if (typeof d.EvLat === "number" && typeof d.EvLon === "number") {
        const key = `${day}|${d.EvLat.toFixed(3)}|${d.EvLon.toFixed(3)}|`;
        const i = S.Locs.findIndex((x) => x.startsWith(key));
        if (i < 0) S.Locs.push(key + (wine ? "v" : ""));
        else if (wine && !S.Locs[i].endsWith("v")) S.Locs[i] += "v";
        if (S.Locs.length > 400) S.Locs.splice(0, S.Locs.length - 400);
      }

      if (partyFriends > 0) progress("vecirek", partyFriends);
      if (partyFriends >= 4) award("vecirek", fmtDay(day));

      // Vánoce
      if (pub && timed && day === "2026-12-23" && (t.min >= 17 * 60 || t.iso !== day)) award("kaprVHospode", d.PubName || "");
      if (pub && timed && day === "2026-12-24" && t.iso === day && t.min < 15 * 60) award("naJednoPredVecery", d.PubName || "");
      if (n0(d.Panaky) > 0 && d.PanakDruh === "Slivovice" && between(day, "2026-12-24", "2026-12-26")) award("slivovicePodStromecek", fmtDay(day));
      if (pub && day === "2026-12-26") award("stepanska", d.PubName || "");
      const mezi = S.Days.filter((x) => between(x, "2026-12-27", "2026-12-30")).length;
      if (mezi > 0) progress("meziSvatky", mezi);
      if (mezi >= 3) award("meziSvatky", "");

      // Silvestr a Nový rok
      if (timed) {
        const nye = t.iso === "2026-12-31", ny = t.iso === "2027-01-01";
        if ((nye && t.min >= 23 * 60 + 30) || (ny && t.min <= 30)) award("pulnoc", "");
        if (wine && ((nye && t.min >= 23 * 60) || (ny && t.min <= 60))) award("bubliny", "");
        if (ny && t.min >= 6 * 60 && t.min < 12 * 60) award("vyprostovak", "");
      }
      if (day === "2027-01-06") {
        kinds.forEach((k) => addTo(S.K6, k));
        progress("triKralove", S.K6.length);
        if (S.K6.length >= 3) award("triKralove", "");
      }
    });
  });

  // ---------------- připíjení ----------------

  out.badgesCheer = onDocumentCreated("Cheers/{id}", async (event) => {
    const c = event.data?.data();
    if (!c || !c.From || !c.To) return;
    const day = prague(c.At?.toDate?.() || new Date()).pubDay;
    if (!inEvent(day)) return;
    await update(c.From, (S, award, progress) => {
      addTo(S.CheerTo, c.To);
      progress("rozdavac", S.CheerTo.length);
      if (S.CheerTo.length >= 10) award("rozdavac", fmtDay(day));
      if (day === "2026-12-24") award("stedrovecerniPripitek", "");
    });
    await update(c.To, (S, award, progress) => {
      addTo(S.CheerFrom, c.From);
      progress("oblibenec", S.CheerFrom.length);
      if (S.CheerFrom.length >= 5) award("oblibenec", fmtDay(day));
    });
  });

  // ---------------- pozvánky (≥ 4 lidi řekli „Jdu“) ----------------

  out.badgesInvite = onDocumentUpdated("Invites/{id}", async (event) => {
    const v = event.data?.after?.data();
    if (!v || v.Version !== 2 || !v.From) return;
    const yes = (st) => Object.values(st || {}).filter((s) => s === "yes").length;
    const before = yes(event.data.before.data()?.Status), now = yes(v.Status);
    if (now <= before) return;
    const day = prague(v.At?.toDate?.() || new Date()).pubDay;
    if (!inEvent(day)) return;
    await update(v.From, (S, award, progress) => {
      progress("vanocniPozvanka", now);
      if (now >= 4) award("vanocniPozvanka", v.PubName || "");
    });
  });

  // ---------------- Suchý start: 6. 1. ráno, kdo 2.–5. 1. nic nezapsal ----------------

  out.badgesSuchyStart = onSchedule({ schedule: "30 7 6 1 *", region: "europe-west3", timeZone: "Europe/Prague" }, async () => {
    if (prague(new Date()).iso !== "2027-01-06") return;
    const snap = await db.collection("AchievementState").get();
    for (const s of snap.docs) {
      const days = s.get("Days") || [];
      if (days.length === 0 || days.some((x) => between(x, "2027-01-02", "2027-01-05"))) continue;
      await update(s.id, (S, award) => award("suchyStart", ""));
    }
  });

  // ---------------- hlášení trhů ----------------

  async function admin() { return ADMIN_UID.value(); }

  /** Zpětně: zaokrouhlené polohy z dob, kdy trh běžel → trhy a odznaky. Tolerance 80 m kvůli zaokrouhlení. */
  async function retro(zone) {
    const snap = await db.collection("AchievementState").get();
    let n = 0;
    for (const s of snap.docs) {
      const hits = (s.get("Locs") || []).map((x) => x.split("|"))
        .filter(([day, lat, lon]) => active(zone, day) && inZone(zone, Number(lat), Number(lon), 80));
      if (hits.length === 0) continue;
      n++;
      await update(s.id, (S, award, progress) => {
        for (const [day, lat, lon, v] of hits) {
          trhDay(S, award, progress, day, zone.Id, v === "v");
          S.Locs = S.Locs.filter((x) => !x.startsWith(`${day}|${lat}|${lon}|`));
        }
      });
    }
    logger.info(`Zpětně trh ${zone.Id}: ${n} lidí`);
  }

  /** Schválí hlášení → kruh kolem jejich středu do nahlášených trhů, přestavba Events/aktualni, zpětné odznaky. */
  async function approve(docs, opts = {}) {
    const pts = docs.map((x) => [x.get("Lat"), x.get("Lon")]);
    const c = [pts.reduce((s, p) => s + p[0], 0) / pts.length, pts.reduce((s, p) => s + p[1], 0) / pts.length];
    const spread = Math.max(0, ...pts.map((p) => distM(p, c)));
    const day = docs[0].get("Day");
    const froms = docs.map((x) => x.get("From")).filter(Boolean).sort();
    const tos = docs.map((x) => x.get("To")).filter(Boolean).sort();
    let from = opts.from || froms[0] || day;
    if (from > day) from = day;
    let to = opts.to || tos[tos.length - 1] || day;
    if (to < day) to = day;
    const note = docs.map((x) => x.get("Note")).find((s) => typeof s === "string" && s.trim());
    const zone = {
      Id: `h-${docs[0].id}`.slice(0, 40),
      Name: (opts.name || note || "Nahlášené trhy").trim().slice(0, 80),
      From: from, To: to,
      Status: opts.auto ? "nahlaseno" : "potvrzeno",
      Src: opts.auto ? "3 hlášení" : "hlášení",
      P: [], L: [], W: 0,
      C: encode([c]), R: Math.round(Math.min(250, Math.max(80, (opts.radius || 0), spread + 60))),
    };
    await db.doc("EventsAdmin/zdroj").set({ Reported: FieldValue.arrayUnion(zone) }, { merge: true });
    const batch = db.batch();
    docs.forEach((x) => batch.update(x.ref, { Status: "approved", TrhId: zone.Id }));
    await batch.commit();
    await rebuild();
    await retro(zone);
    const reporters = [...new Set(docs.map((x) => x.get("By")))];
    await notifyData(reporters, {
      type: "trh", event: EVENT.Id,
      title: `Trhy schválené: ${zone.Name}`,
      body: "Díky! Odznaky za ně se dorovnají samy.",
    });
    return zone;
  }

  out.trhReport = onDocumentCreated("TrhReports/{id}", async (event) => {
    const r = event.data?.data();
    const ref = event.data?.ref;
    if (!r || !r.By || typeof r.Lat !== "number" || typeof r.Lon !== "number") return;
    const day = typeof r.Day === "string" ? r.Day : prague(new Date()).pubDay;

    // už v seznamu?
    const known = (await zones()).find((z) => active(z, day) && inZone(z, r.Lat, r.Lon, 40));
    if (known) {
      await ref.update({ Status: "known", TrhId: known.Id });
      return;
    }

    // automatické schválení: stejné místo (do 250 m) týž den nahlásili 3 různí lidé
    const same = await db.collection("TrhReports").where("Day", "==", day).get();
    const near = same.docs.filter((x) => ["pending", undefined].includes(x.get("Status")) || x.id === event.params.id)
      .filter((x) => distM([x.get("Lat"), x.get("Lon")], [r.Lat, r.Lon]) <= 250);
    const people = new Set(near.map((x) => x.get("By")));
    const adminUid = await admin();
    if (people.size >= 3) {
      const zone = await approve(near, { auto: true });
      if (adminUid) {
        await notifyData([adminUid], {
          type: "trh", event: EVENT.Id,
          title: `Trhy schválené samy: ${zone.Name}`,
          body: `Nahlásili ${people.size} lidi. Termín ${fmtDay(zone.From)}–${fmtDay(zone.To)} – oprav v Nastavení → Návrhy trhů.`,
        });
      }
      return;
    }

    await ref.update({ Status: "pending" });
    if (adminUid) {
      const when = r.From || r.To ? ` (${r.From ? fmtDay(r.From) : "?"}–${r.To ? fmtDay(r.To) : "?"})` : "";
      await notifyData([adminUid], {
        type: "trh", event: EVENT.Id,
        title: `Hlášení trhů od ${r.ByNick || "?"}${when}`,
        body: `${r.Note ? r.Note + " · " : ""}Schválíš v Nastavení → Návrhy trhů (${people.size}/3 do automatického).`,
      });
    }
  });

  // ---------------- správce: schválit / zamítnout / opravit trh / přestavět ----------------

  out.trhAction = onCall(async (request) => {
    const me = request.auth?.uid;
    if (!me || me !== (await admin())) throw new HttpsError("permission-denied", "Jen pro správce.");
    const a = request.data || {};
    const isoOk = (s) => s == null || (typeof s === "string" && /^\d{4}-\d{2}-\d{2}$/.test(s));
    if (!isoOk(a.from) || !isoOk(a.to)) throw new HttpsError("invalid-argument", "Datum ve tvaru 2026-12-24.");

    if (a.action === "approve" || a.action === "reject") {
      if (typeof a.id !== "string") throw new HttpsError("invalid-argument", "Chybí hlášení.");
      const doc = await db.doc(`TrhReports/${a.id}`).get();
      if (!doc.exists) throw new HttpsError("not-found", "Hlášení už neexistuje.");
      if (a.action === "reject") {
        await doc.ref.update({ Status: "rejected" });
        return { ok: true };
      }
      // s ním i ostatní čekající hlášení ze stejného místa a dne
      const same = await db.collection("TrhReports").where("Day", "==", doc.get("Day")).get();
      const near = [doc, ...same.docs.filter((x) => x.id !== doc.id && x.get("Status") === "pending"
        && distM([x.get("Lat"), x.get("Lon")], [doc.get("Lat"), doc.get("Lon")]) <= 250)];
      const zone = await approve(near, { name: a.name, from: a.from, to: a.to, radius: Number(a.radius) || 0 });
      return { ok: true, trhId: zone.Id };
    }

    // oprava trhu ze seznamu (termín vygooglený, přejmenování, stav, smazání)
    if (a.action === "edit") {
      if (typeof a.trhId !== "string") throw new HttpsError("invalid-argument", "Chybí trh.");
      const patch = {};
      if (a.from) patch.From = a.from;
      if (a.to) patch.To = a.to;
      if (typeof a.name === "string" && a.name.trim()) patch.Name = a.name.trim().slice(0, 80);
      if (["potvrzeno", "odhad", "nahlaseno"].includes(a.status)) patch.Status = a.status;
      if (a.remove === true) patch.Removed = true;
      await db.doc("EventsAdmin/zdroj").set({ Overrides: { [a.trhId]: patch } }, { merge: true });
      const trhy = await rebuild();
      const z = trhy.find((x) => x.Id === a.trhId);
      if (z && (patch.From || patch.To)) await retro(z);   // delší termín → zpětně odznaky
      return { ok: true };
    }

    if (a.action === "sync") {
      const trhy = await rebuild();
      return { ok: true, trhy: trhy.length };
    }

    throw new HttpsError("invalid-argument", "Neznámá akce.");
  });

  // pro testy (index.js je neexportuje)
  out._test = { prague, inZone, decode, encode, fromBundle, BADGES, NORMAL };
  return out;
};
