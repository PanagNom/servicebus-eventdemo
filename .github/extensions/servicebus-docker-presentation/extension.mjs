import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CanvasError, createCanvas, joinSession } from '@github/copilot-sdk/extension';
import declaration from './extension.json' with { type: 'json' };

const directory = path.dirname(fileURLToPath(import.meta.url));
const liveInstances = new Map();
let session;

async function readAsset(name) {
  const fullPath = path.resolve(directory, name);
  if (!fullPath.startsWith(directory + path.sep)) {
    throw new CanvasError('invalid_asset', 'The requested presentation asset is outside the extension.');
  }
  return fs.readFile(fullPath);
}

async function openPresentation({ instanceId }) {
  if (!instanceId || typeof instanceId !== 'string') {
    throw new CanvasError('invalid_instance', 'A stable instanceId is required.');
  }

  const existing = liveInstances.get(instanceId);
  if (existing) {
    return existing.url;
  }

  const server = http.createServer(async (request, response) => {
    try {
      const url = new URL(request.url || '/', 'http://127.0.0.1');
      let name = 'presentation.html';
      if (url.pathname === '/presentation.css') name = 'presentation.css';
      if (url.pathname === '/presentation.js') name = 'presentation.js';
      if (!['presentation.html', 'presentation.css', 'presentation.js'].includes(name) &&
          url.pathname !== '/') {
        response.writeHead(404, { 'content-type': 'text/plain' });
        response.end('Not found');
        return;
      }

      const content = await readAsset(name);
      const contentType = name.endsWith('.css')
        ? 'text/css; charset=utf-8'
        : name.endsWith('.js')
          ? 'text/javascript; charset=utf-8'
          : 'text/html; charset=utf-8';
      response.writeHead(200, {
        'content-type': contentType,
        'cache-control': 'no-store'
      });
      response.end(content);
    } catch (error) {
      session.log(`Presentation server error: ${error.message}`);
      response.writeHead(500, { 'content-type': 'text/plain' });
      response.end('Unable to load presentation');
    }
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });

  const address = server.address();
  if (!address || typeof address === 'string') {
    server.close();
    throw new CanvasError('server_error', 'Could not start the presentation server.');
  }

  const live = {
    server,
    url: `http://127.0.0.1:${address.port}/`
  };
  liveInstances.set(instanceId, live);
  return live.url;
}

async function closePresentation({ instanceId }) {
  const live = liveInstances.get(instanceId);
  if (!live) return;
  liveInstances.delete(instanceId);
  await new Promise(resolve => live.server.close(resolve));
}

session = await joinSession({
  canvases: declaration.canvases.map(canvas => createCanvas({
    ...canvas,
    open: openPresentation,
    onClose: closePresentation
  }))
});
