// Build from the locked npm tree. CI may supply OFFICE_SURFACE_BUILD_ROOT to keep
// node_modules outside the WPF project. Only public/ and files.json are runtime inputs.
import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
const root = path.dirname(fileURLToPath(import.meta.url));
const tooling = path.resolve(process.env.OFFICE_SURFACE_BUILD_ROOT || root);
const require = createRequire(path.join(tooling, 'package.json'));
const modules = path.join(tooling, 'node_modules');
const output = path.join(root, 'public');
const vendor = path.join(output, 'app');
const lib = name => path.join(modules, ...name.split('/'));
await fs.mkdir(vendor, { recursive: true });
const result = await require('esbuild').build({
  absWorkingDir: root, entryPoints: ['surface.js'], outfile: path.join(vendor, 'surface.js'),
  bundle: true, minify: true, format: 'esm', platform: 'browser', target: 'es2022',
  nodePaths: [modules], metafile: true, external: ['node:*'],
});
const core = lib('@docx-editor.dev/core');
const fonts = lib('@docx-editor.dev/fonts');
await fs.copyFile(path.join(core, 'dist/harfbuzz.wasm'), path.join(vendor, 'harfbuzz.wasm'));
await fs.copyFile(path.join(core, 'dist/editor.css'), path.join(vendor, 'editor.css'));
await fs.cp(path.join(fonts, 'assets'), path.join(output, 'assets'), { recursive: true });
const packages = new Set(['@docx-editor.dev/core', '@docx-editor.dev/fonts']);
for (const name of Object.keys(result.metafile.inputs)) {
  const source = name.split('node_modules/').at(-1);
  if (source === name) continue;
  const parts = source.split('/');
  packages.add(parts[0].startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]);
}
for (const name of [...packages].sort()) {
  const folder = lib(name);
  const dest = path.join(output, 'licenses', name.replace('/', '__'));
  await fs.mkdir(dest, {recursive:true});
  const metadata = JSON.parse(await fs.readFile(path.join(folder, 'package.json'), 'utf8'));
  await fs.writeFile(path.join(dest, 'package.txt'), name + ' ' + metadata.version + '\n' + metadata.license + '\n');
  const notices = (await fs.readdir(folder)).filter(f => /^(license|licence|copying|notice|third.party.notices)/i.test(f));
  if (name === '@nodable/entities' && !notices.length) {
    await fs.copyFile(path.join(root,'licenses/nodable-entities.txt'), path.join(dest,'LICENSE'));
  } else if (!notices.length) throw new Error('Missing license: ' + name);
  for (const file of notices) await fs.cp(path.join(folder,file), path.join(dest,file), {recursive:true});
  if (name === 'pako') {
    // The package-wide LICENSE covers its JS wrapper. The port of zlib carries
    // the separate upstream attribution and conditions in its source header.
    const source = await fs.readFile(path.join(folder, 'lib/zlib/inflate.js'), 'utf8');
    const notice = source.match(/^\/\/ \(C\) 1995[^\n]*(?:\n\/\/[^\n]*)*/m)?.[0];
    if (!notice?.includes('source distribution.')) throw new Error('Missing zlib source notice');
    await fs.writeFile(path.join(dest,'ZLIB-NOTICE.txt'), notice.trimEnd()+'\n');
  }
}
async function list(dir) {
  const files = [];
  for (const entry of await fs.readdir(dir, {withFileTypes:true})) {
    const target = path.join(dir,entry.name);
    if (entry.isDirectory()) files.push(...await list(target));
    else files.push(target);
  }
  return files;
}
const Files = [];
for (const file of (await list(output)).sort()) {
  const bytes = await fs.readFile(file);
  Files.push({Name:path.relative(output,file).split(path.sep).join('/'), Bytes:bytes.length,
    Sha256:crypto.createHash('sha256').update(bytes).digest('hex')});
}
await fs.writeFile(path.join(root,'files.json'), JSON.stringify({Version:'2.35.0',Files}, null, 2)+'\n');
