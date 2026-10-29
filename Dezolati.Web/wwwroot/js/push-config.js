// Oznámení na webu (web push přes Firebase Cloud Messaging).
// Hodnoty z Firebase konzole: Project settings → Your apps → Web (appId) a Cloud Messaging → Web Push certificates (vapidKey).
// Obojí je veřejné – patří do webu, žádné tajemství. Prázdné = oznámení na webu vypnutá.
window.dzPushConfig = {
    apiKey: "AIzaSyBmfjw3PRo85_PDq8Z763rmh49pags1PjA",
    authDomain: "dezolati.firebaseapp.com",
    projectId: "dezolati",
    messagingSenderId: "27594600820",
    appId: "1:27594600820:web:01cf2b9be27326bec73c21",
    vapidKey: "BMit2ONzEeHqSTByhR-gchpq88_9WUPvcIz-cekHPeI65i53ZPew84pf3MGNq3CflRkp4WbJVP8i0r4DvkbnMU8"
};
