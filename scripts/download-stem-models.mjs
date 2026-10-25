import fs from 'node:fs';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { Readable } from 'node:stream';
import { createHash } from 'node:crypto';
import { ROOT } from '../server.mjs';

export const UVR_COMMIT = '5517e0cf0d1acd16a1618eeedec596957523f9e1';
export const MDX_NAME = 'UVR-MDX-NET-Inst_HQ_5.onnx';

async function download(url, target) {
  if (fs.existsSync(target)) return;
  fs.mkdirSync(path.dirname(target), { recursive: true });
  console.log(`Downloading ${path.basename(target)}…`);
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Download failed (${response.status}): ${url}`);
  await pipeline(Readable.fromWeb(response.body), fs.createWriteStream(target + '.partial'));
  fs.renameSync(target + '.partial', target);
}

export async function downloadModels() {
  const base = path.join(ROOT, 'tools', 'stem-models');
  await download('https://dl.fbaipublicfiles.com/demucs/hybrid_transformer/955717e8-8726e21a.th', path.join(base, 'demucs', '955717e8-8726e21a.th'));
  await download('https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/htdemucs.yaml', path.join(base, 'demucs', 'htdemucs.yaml'));
  await download(`https://github.com/TRvlvr/model_repo/releases/download/all_public_uvr_models/${MDX_NAME}`, path.join(base, 'mdx', MDX_NAME));
  // UVR identifies MDX model parameters using MD5 of the final 10,000 KiB.
  const model = fs.readFileSync(path.join(base, 'mdx', MDX_NAME));
  const modelHash = createHash('md5').update(model.subarray(Math.max(0, model.length - 10000 * 1024))).digest('hex');
  const response = await fetch('https://raw.githubusercontent.com/TRvlvr/application_data/main/mdx_model_data/model_data_new.json');
  if (!response.ok) throw new Error('Could not retrieve UVR model parameters.');
  const data = await response.json();
  const params = data[modelHash];
  if (!params || params.primary_stem !== 'Instrumental') throw new Error(`UVR does not recognize the Inst HQ 5 model hash: ${modelHash}`);
  fs.writeFileSync(path.join(base, 'mdx', 'config.json'), JSON.stringify({ filename: MDX_NAME, modelHash, ...params }, null, 2));
  const receipts = {};
  for (const [label, file] of Object.entries({ htdemucs: path.join(base, 'demucs', '955717e8-8726e21a.th'), mdx: path.join(base, 'mdx', MDX_NAME) })) {
    const bytes = fs.readFileSync(file);
    receipts[label] = { filename: path.basename(file), sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length };
  }
  if (!receipts.htdemucs.sha256.startsWith('8726e21a')) throw new Error('Demucs model checksum does not match its official filename.');
  fs.writeFileSync(path.join(base, 'model-receipts.json'), JSON.stringify({ uvrCommit: UVR_COMMIT, models: receipts, mdxParameters: params }, null, 2));
  console.log('Exact htdemucs and UVR-MDX-NET Inst HQ 5 models are ready.');
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.join(ROOT, 'scripts', 'download-stem-models.mjs')) {
  await downloadModels();
}
