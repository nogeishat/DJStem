import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { relocate, relocateState, atomicJson } from './state.mjs';

test('relocates values, arrays and dictionary keys without rewriting unrelated text', () => {
  const source = { audio: 'X:\\Music\\StemKit\\mp3\\Aminé\\A.mp3', Sources: [null, 'x:/Music/StemKit/mp3/A/B.mp3'], KeyShifts: { 'X:\\Music\\StemKit\\mp3\\A\\B.mp3': 3 }, title: 'X:\\Music\\StemKitty\\song', url: 'https://example.com/X:/Music/StemKit' };
  const result = relocate(source, [['X:\\Music\\StemKit', 'E:\\StemKit USB\\library']]);
  assert.equal(result.audio, 'E:\\StemKit USB\\library\\mp3\\Aminé\\A.mp3');
  assert.equal(result.Sources[0], null);
  assert.equal(result.Sources[1], 'E:\\StemKit USB\\library\\mp3\\A\\B.mp3');
  assert.equal(result.KeyShifts['E:\\StemKit USB\\library\\mp3\\A\\B.mp3'], 3);
  assert.equal(result.title, source.title);
  assert.equal(result.url, source.url);
  assert.equal(source.audio, 'X:\\Music\\StemKit\\mp3\\Aminé\\A.mp3');
});

test('retries interrupted moves and retains the original pre-move settings', () => {
  const scratch = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..', 'data');
  fs.mkdirSync(scratch, { recursive: true });
  const root = fs.mkdtempSync(path.join(scratch, 'portable-state-test-'));
  try {
    atomicJson(path.join(root, 'data', 'portable-location.json'), { projectRoot: 'C:\\Original', libraryRoots: ['D:\\Music\\StemKit'] });
    const file = path.join(root, 'data', 'player-settings.json');
    atomicJson(file, { audio: 'D:\\Music\\StemKit\\mp3\\A\\B.mp3' });
    relocateState(root);
    assert.equal(JSON.parse(fs.readFileSync(file)).audio, path.join(root, 'library') + '\\mp3\\A\\B.mp3');
    const backup = fs.readFileSync(file + '.before-portable-move', 'utf8');
    relocateState(root);
    assert.equal(fs.readFileSync(file + '.before-portable-move', 'utf8'), backup);
    // Simulate one unconverted file remaining after a power interruption.
    atomicJson(path.join(root, 'data', 'queue.json'), { path: 'D:\\Music\\StemKit\\stems\\A\\B' });
    relocateState(root);
    assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'data', 'queue.json'))).path, path.join(root, 'library') + '\\stems\\A\\B');
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
