// Při vývoji (dotnet run) nic necachovat, ať je vidět každá změna. Oznámení ale ano.
self.importScripts('./push-sw.js');
self.addEventListener('fetch', () => { });
