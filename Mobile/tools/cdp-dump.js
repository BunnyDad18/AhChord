// CDP dump of the AhChord app's IndexedDB library cache over Wi-Fi adb forward.
// usage: node cdp-dump.js <port> <outFile>
const [port, outFile] = [process.argv[2], process.argv[3]];
const fs = require('fs');

const expr = `(async () => {
  const db = await new Promise((res, rej) => {
    const r = indexedDB.open('ahchord-offline');
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
  const all = (s) => new Promise((res, rej) => {
    const rq = db.transaction(s).objectStore(s).getAll();
    rq.onsuccess = () => res(rq.result);
    rq.onerror = () => rej(rq.error);
  });
  const metaGet = (k) => new Promise((res, rej) => {
    const rq = db.transaction('meta').objectStore('meta').get(k);
    rq.onsuccess = () => res(rq.result);
    rq.onerror = () => rej(rq.error);
  });
  const dump = {
    exportedAt: new Date().toISOString(),
    songs: await all('songs'),
    playlists: await all('playlists'),
    collections: (await metaGet('collections')) ?? [],
  };
  return JSON.stringify(dump);
})()`;

async function main() {
  const pages = await (await fetch(`http://localhost:${port}/json`)).json();
  const page = pages.find((p) => p.type === 'page' && p.url === 'http://localhost/');
  if (!page) throw new Error('AhChord page not found: ' + pages.map((p) => p.url).join(', '));

  const ws = new WebSocket(page.webSocketDebuggerUrl.replace('localhost', '127.0.0.1'));
  const json = await new Promise((resolve, reject) => {
    ws.addEventListener('open', () =>
      ws.send(JSON.stringify({
        id: 1,
        method: 'Runtime.evaluate',
        params: { expression: expr, returnByValue: true, awaitPromise: true, timeout: 120000 },
      })),
    );
    ws.addEventListener('message', (e) => {
      const msg = JSON.parse(e.data);
      if (msg.id !== 1) return;
      ws.close();
      if (msg.error) return reject(new Error(JSON.stringify(msg.error)));
      const r = msg.result;
      if (r.exceptionDetails) return reject(new Error('page exception: ' + JSON.stringify(r.exceptionDetails).slice(0, 500)));
      resolve(r.result.value);
    });
    ws.addEventListener('error', () => reject(new Error('ws error')));
  });

  fs.writeFileSync(outFile, json);
  const d = JSON.parse(json);
  console.log(`DUMPED -> ${outFile} (${(json.length / 1024).toFixed(0)} KB)`);
  console.log(`songs: ${d.songs.length}  playlists: ${d.playlists.length}  collections: ${(d.collections || []).length}`);
  console.log('withChordProText:', d.songs.filter((s) => s.chordProText).length);
  console.log('sample songs:', d.songs.slice(0, 6).map((s) => `${s.title}${s.artist ? ' — ' + s.artist : ''}`).join(' | '));
  console.log('playlists:', d.playlists.map((p) => `${p.name} (${(p.songs || []).length})`).join(' | '));
}

main().catch((e) => { console.error('DUMP FAILED:', e.message); process.exit(1); });
