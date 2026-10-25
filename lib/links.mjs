import { createHash } from 'node:crypto';

const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;
const HOSTS = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtube-nocookie.com', 'www.youtube-nocookie.com']);
const SOUNDCLOUD_HOSTS = new Set(['soundcloud.com', 'www.soundcloud.com', 'm.soundcloud.com']);
const SOUNDCLOUD_PAGES = new Set(['tracks', 'albums', 'sets', 'reposts', 'likes', 'spotlight', 'comments', 'popular-tracks', 'recommended']);
const SOUNDCLOUD_ROOTS = new Set(['discover', 'stream', 'search', 'charts', 'stations', 'tags', 'you', 'upload', 'settings']);
const TRACK_ID = /^\d{1,20}$/;

export function isSongId(id) {
  return typeof id === 'string' && (id.startsWith('soundcloud-') ? /^soundcloud-\d{1,20}$/.test(id) : VIDEO_ID.test(id));
}

function soundcloudLink(url) {
  const host = url.hostname.toLowerCase();
  let canonical, identity;
  if (host === 'on.soundcloud.com') {
    if (!/^\/[A-Za-z0-9_-]+\/?$/.test(url.pathname)) return null;
    canonical = `https://on.soundcloud.com${url.pathname.replace(/\/$/, '')}`;
    identity = canonical;
  } else if (SOUNDCLOUD_HOSTS.has(host)) {
    const parts = url.pathname.replace(/\/$/, '').split('/').slice(1);
    if (parts.length < 2 || parts.length > 3 || !parts.slice(0, 2).every(part => /^[\p{L}\p{N}_-]+$/u.test(part))) return null;
    const [artist, track] = parts.slice(0, 2).map(part => part.toLowerCase());
    if (SOUNDCLOUD_ROOTS.has(artist) || SOUNDCLOUD_PAGES.has(track)) return null;
    const token = parts[2] || url.searchParams.get('secret_token');
    if (token && !/^s-[A-Za-z0-9_-]+$/.test(token)) return null;
    identity = `https://soundcloud.com/${artist}/${track}`;
    canonical = identity + (token ? `/${token}` : '');
  } else if (host === 'api.soundcloud.com' || host === 'api-v2.soundcloud.com') {
    const match = /^\/tracks\/(?:soundcloud%3Atracks%3A)?(\d{1,20})\/?$/i.exec(url.pathname);
    if (!match) return null;
    const token = url.searchParams.get('secret_token');
    if (token && !/^s-[A-Za-z0-9_-]+$/.test(token)) return null;
    canonical = `https://api.soundcloud.com/tracks/${match[1]}` + (token ? `?secret_token=${token}` : '');
    return { id: `soundcloud-${match[1]}`, url: canonical, source: 'soundcloud' };
  } else return null;
  // A permalink does not reveal SoundCloud's numeric track ID until metadata is read.
  return { id: `soundcloud-link-${createHash('sha256').update(identity).digest('hex').slice(0, 32)}`, url: canonical, source: 'soundcloud' };
}

export function soundcloudTrackId(id) {
  return typeof id === 'string' && TRACK_ID.test(id) ? `soundcloud-${id}` : null;
}

export async function resolveSoundcloudLink(link, fetchUrl = fetch) {
  if (link?.source !== 'soundcloud') throw new Error('Invalid SoundCloud track link.');
  for (let redirects = 0; redirects < 5; redirects++) {
    if (new URL(link.url).hostname !== 'on.soundcloud.com') return link;
    const response = await fetchUrl(link.url, { redirect: 'manual', signal: AbortSignal.timeout(15000) });
    const location = response.headers.get('location');
    await response.body?.cancel();
    if (response.status < 300 || response.status >= 400 || !location) throw new Error('Could not resolve this SoundCloud share link. Drop the full track address instead.');
    const target = normalizeLink(new URL(location, link.url).href);
    if (target?.source !== 'soundcloud') throw new Error('This SoundCloud share link does not point to a single track.');
    link = target;
    if (new URL(link.url).hostname !== 'on.soundcloud.com') return link;
  }
  throw new Error('Too many redirects in this SoundCloud share link. Drop the full track address instead.');
}

export function normalizeLink(input) {
  let url;
  try { url = new URL(input); } catch { return null; }
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || url.port) return null;
  const host = url.hostname.toLowerCase();
  const soundcloud = soundcloudLink(url);
  if (soundcloud) return soundcloud;
  let id;
  if (host === 'youtu.be' || host === 'www.youtu.be') {
    id = url.pathname.split('/')[1];
  } else if (HOSTS.has(host)) {
    if (url.pathname === '/watch') id = url.searchParams.get('v');
    else if (/^\/(shorts|live|embed)\//.test(url.pathname)) id = url.pathname.split('/')[2];
  }
  return VIDEO_ID.test(id ?? '') ? { id, url: `https://www.youtube.com/watch?v=${id}` } : null;
}

export function extractLinks(text) {
  if (typeof text !== 'string') throw new Error('Provide links as text.');
  const candidates = text.match(/https?:\/\/[^\s<>"']+/gi) ?? [];
  const links = new Map();
  const rejected = [];
  for (const candidate of candidates) {
    const cleaned = candidate.replace(/&amp;/g, '&').replace(/[),.;\]}]+$/, '');
    const link = normalizeLink(cleaned);
    if (link) links.set(link.id, link);
    else rejected.push(cleaned);
  }
  return { links: [...links.values()], rejected, repeated: candidates.length - rejected.length - links.size };
}
