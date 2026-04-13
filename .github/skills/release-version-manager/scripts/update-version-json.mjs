#!/usr/bin/env node
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

function printHelp() {
  console.log(`Usage: node .github/skills/release-version-manager/scripts/update-version-json.mjs [options]

version.json を更新します。既定は dry-run です。

Options:
  --target <path>         対象ファイル（既定: version.json）
  --version <value>       次バージョン
  --previous <value>      差分起点バージョン
  --summary <text>        概要文
  --highlight <value>     "タイトル::説明" の形式。複数指定可
  --write                 実際に書き込む
  -h, --help              ヘルプを表示
`);
}

function parseArgs(argv) {
  const opts = {
    target: 'version.json',
    version: '',
    previous: '',
    summary: '',
    highlights: [],
    write: false,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--target') {
      const value = argv[i + 1];
      if (!value) throw new Error('--target には値が必要です');
      opts.target = value;
      i += 1;
      continue;
    }
    if (arg === '--version') {
      const value = argv[i + 1];
      if (!value) throw new Error('--version には値が必要です');
      opts.version = value;
      i += 1;
      continue;
    }
    if (arg === '--previous') {
      const value = argv[i + 1];
      if (!value) throw new Error('--previous には値が必要です');
      opts.previous = value;
      i += 1;
      continue;
    }
    if (arg === '--summary') {
      const value = argv[i + 1];
      if (!value) throw new Error('--summary には値が必要です');
      opts.summary = value;
      i += 1;
      continue;
    }
    if (arg === '--highlight') {
      const value = argv[i + 1];
      if (!value) throw new Error('--highlight には値が必要です');
      opts.highlights.push(value);
      i += 1;
      continue;
    }
    if (arg === '--write') {
      opts.write = true;
      continue;
    }
    if (arg === '-h' || arg === '--help') {
      opts.help = true;
      continue;
    }
    throw new Error(`不明なオプション: ${arg}`);
  }

  return opts;
}

function parseHighlight(input) {
  const [title, description] = input.split('::');
  if (!title || !description) {
    throw new Error(`--highlight は "タイトル::説明" 形式で指定してください: ${input}`);
  }
  return { title: title.trim(), description: description.trim() };
}

async function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`Error: ${error instanceof Error ? error.message : String(error)}`);
    printHelp();
    process.exit(1);
  }

  if (opts.help) {
    printHelp();
    return;
  }

  if (!opts.version || !opts.previous || !opts.summary || opts.highlights.length === 0) {
    throw new Error('--version / --previous / --summary / --highlight はすべて必須です');
  }

  const targetPath = path.resolve(process.cwd(), opts.target);
  const currentRaw = await readFile(targetPath, 'utf8');
  const current = JSON.parse(currentRaw);
  const next = {
    ...current,
    version: opts.version,
    history: {
      previousVersion: opts.previous,
      summary: opts.summary,
      highlights: opts.highlights.map(parseHighlight),
    },
  };

  const output = `${JSON.stringify(next, null, 2)}\n`;
  if (!opts.write) {
    process.stdout.write(output);
    return;
  }

  await writeFile(targetPath, output, 'utf8');
  console.log(`Updated: ${targetPath}`);
}

main().catch((error) => {
  console.error(`Unexpected error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
