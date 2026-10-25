import path from 'node:path';

// Application code/runtimes and a person's library have independent lifetimes.
export function userRoot(root) {
  return process.env.STEMKIT_USER_DATA ? path.resolve(process.env.STEMKIT_USER_DATA) : root;
}
export const dataPath = (root, ...parts) => path.join(userRoot(root), 'data', ...parts);
export const settingsPath = root => path.join(userRoot(root), 'settings.json');
